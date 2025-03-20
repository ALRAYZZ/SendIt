using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace SendIt
{
	public partial class MainWindow : Window
	{
		private TcpListener server;
		private TcpClient client;
		private NetworkStream stream;
		private bool isRunning;

		public MainWindow()
		{
			InitializeComponent();
		}

		// Start listening for connections
		private async void StartServer_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (isRunning)
				{
					MessageBox.Show("Server already running!");
					return;
				}

				server = new TcpListener(IPAddress.Any, 5000);
				server.Start();
				isRunning = true;
				StatusText.Text = "Server started, waiting for connections...";

				await Task.Run(async () =>
				{
					while (isRunning)
					{
						TcpClient newClient = await server.AcceptTcpClientAsync();
						HandleConnection(newClient);
					}
				});
			}
			catch (Exception ex)
			{
				StatusText.Text = "Error starting server";
				MessageBox.Show($"Server error: {ex.Message}");
				ShutdownServer();
			}
		}

		// Stop the server
		private void StopServer_Click(object sender, RoutedEventArgs e)
		{
			ShutdownServer();
			StatusText.Text = "Server stopped";
			MessageBox.Show("Server stopped");
		}

		private void ShutdownServer()
		{
			isRunning = false;
			server?.Stop();
			client?.Close();
			stream?.Close();
			client = null;
			stream = null;
		}

		// Connect to a friend
		private async void Connect_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (string.IsNullOrEmpty(FriendAddress.Text))
				{
					MessageBox.Show("Enter friend's IP:Port");
					return;
				}

				string[] parts = FriendAddress.Text.Split(':');
				string ip = parts[0];
				int port = int.Parse(parts[1]);

				client = new TcpClient();
				await client.ConnectAsync(ip, port);
				stream = client.GetStream();

				StatusText.Text = "Requesting connection...";
				await stream.WriteAsync(new byte[] { 1 }, 0, 1); // Connection request

				byte[] response = new byte[1];
				await stream.ReadAsync(response, 0, 1);
				if (response[0] == 1)
				{
					StatusText.Text = $"Connected to {FriendAddress.Text}";
					MessageBox.Show("Connection accepted!");
					ListenForMessages();
				}
				else
				{
					throw new Exception("Connection rejected");
				}
			}
			catch (Exception ex)
			{
				StatusText.Text = "Connection failed";
				MessageBox.Show($"Connection error: {ex.Message}");
				client?.Close();
				stream?.Close();
				client = null;
				stream = null;
			}
		}

		// Handle incoming connections
		private async void HandleConnection(TcpClient newClient)
		{
			try
			{
				NetworkStream newStream = newClient.GetStream();
				string remoteAddress = ((IPEndPoint)newClient.Client.RemoteEndPoint).ToString();

				byte[] command = new byte[1];
				await newStream.ReadAsync(command, 0, 1);

				if (command[0] == 1) // Connection request
				{
					Dispatcher.Invoke(() =>
					{
						var result = MessageBox.Show($"Accept connection from {remoteAddress}?",
							"Connection Request", MessageBoxButton.YesNo);
						byte[] response = new byte[] { (byte)(result == MessageBoxResult.Yes ? 1 : 0) };
						newStream.Write(response, 0, 1);
						newStream.Flush();

						if (result == MessageBoxResult.Yes)
						{
							client = newClient;
							stream = newStream;
							StatusText.Text = $"Connected to {remoteAddress}";
							ListenForMessages();
						}
						else
						{
							newClient.Close();
						}
					});
				}
			}
			catch (Exception ex)
			{
				Dispatcher.Invoke(() => StatusText.Text = $"Connection error: {ex.Message}");
				newClient.Close();
			}
		}

		// Listen for incoming messages/files
		private async void ListenForMessages()
		{
			try
			{
				while (client.Connected)
				{
					byte[] command = new byte[1];
					int bytesRead = await stream.ReadAsync(command, 0, 1);
					if (bytesRead == 0) throw new Exception("Disconnected");

					if (command[0] == 2) // File
					{
						await ReceiveFile();
					}
					else if (command[0] == 3) // Message
					{
						await ReceiveMessage();
					}
				}
			}
			catch (Exception ex)
			{
				Dispatcher.Invoke(() =>
				{
					StatusText.Text = $"Disconnected: {ex.Message}";
					client?.Close();
					stream?.Close();
					client = null;
					stream = null;
				});
			}
		}

		// Send a file (without request/acceptance)
		private async void SendFile_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (client == null || !client.Connected)
				{
					MessageBox.Show("Not connected!");
					return;
				}

				Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
				if (dlg.ShowDialog() != true) return;

				string filePath = dlg.FileName;
				string fileName = Path.GetFileName(filePath);
				byte[] fileData = File.ReadAllBytes(filePath);

				StatusText.Text = "Sending file...";
				await stream.WriteAsync(new byte[] { 2 }, 0, 1); // File command

				// Send file name
				byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
				await stream.WriteAsync(BitConverter.GetBytes(nameBytes.Length), 0, 4);
				await stream.WriteAsync(nameBytes, 0, nameBytes.Length);

				// Send file size and data directly (no waiting for response)
				await stream.WriteAsync(BitConverter.GetBytes(fileData.Length), 0, 4);
				await stream.WriteAsync(fileData, 0, fileData.Length);
				await stream.FlushAsync();

				StatusText.Text = "File sent successfully!";
			}
			catch (Exception ex)
			{
				StatusText.Text = "File send failed";
				MessageBox.Show($"Send error: {ex.Message}");
			}
		}

		// Receive a file (without acceptance prompt)
		private async Task ReceiveFile()
		{
			try
			{
				// Get file name
				byte[] nameLengthBytes = new byte[4];
				int bytesRead = await stream.ReadAsync(nameLengthBytes, 0, 4);
				if (bytesRead != 4) throw new Exception("Failed to read file name length");
				int nameLength = BitConverter.ToInt32(nameLengthBytes, 0);
				byte[] nameBytes = new byte[nameLength];
				bytesRead = await stream.ReadAsync(nameBytes, 0, nameLength);
				if (bytesRead != nameLength) throw new Exception("Failed to read file name");
				string fileName = Encoding.UTF8.GetString(nameBytes);

				Dispatcher.Invoke(() => StatusText.Text = $"Receiving file: {fileName}");

				// Get file size and data
				byte[] sizeBytes = new byte[4];
				bytesRead = await stream.ReadAsync(sizeBytes, 0, 4);
				if (bytesRead != 4) throw new Exception("Failed to read file size");
				int fileSize = BitConverter.ToInt32(sizeBytes, 0);
				byte[] fileData = new byte[fileSize];
				int totalRead = 0;

				while (totalRead < fileSize)
				{
					int read = await stream.ReadAsync(fileData, totalRead, fileSize - totalRead);
					if (read == 0) throw new Exception("Connection lost during file transfer");
					totalRead += read;
					Dispatcher.Invoke(() =>
						StatusText.Text = $"Receiving file... ({totalRead}/{fileSize} bytes)");
				}

				string path = Path.Combine(Directory.GetCurrentDirectory(), $"received_{fileName}");
				File.WriteAllBytes(path, fileData);
				Dispatcher.Invoke(() => StatusText.Text = $"File saved to {path}");
			}
			catch (Exception ex)
			{
				Dispatcher.Invoke(() => StatusText.Text = $"Receive file error: {ex.Message}");
			}
		}

		// Send a message
		private async void SendMessage_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (client == null || !client.Connected)
				{
					MessageBox.Show("Not connected!");
					return;
				}

				string message = Microsoft.VisualBasic.Interaction.InputBox("Enter message:", "Send Message");
				if (string.IsNullOrEmpty(message)) return;

				byte[] data = Encoding.UTF8.GetBytes(message);
				await stream.WriteAsync(new byte[] { 3 }, 0, 1); // Message command
				await stream.WriteAsync(BitConverter.GetBytes(data.Length), 0, 4);
				await stream.WriteAsync(data, 0, data.Length);
				await stream.FlushAsync();

				ChatText.Dispatcher.Invoke(() => ChatText.Text += $"\nYou: {message}");
				StatusText.Text = "Message sent!";
			}
			catch (Exception ex)
			{
				StatusText.Text = "Message send failed";
				MessageBox.Show($"Message error: {ex.Message}");
			}
		}

		// Receive a message
		private async Task ReceiveMessage()
		{
			byte[] lengthBytes = new byte[4];
			await stream.ReadAsync(lengthBytes, 0, 4);
			int length = BitConverter.ToInt32(lengthBytes, 0);
			byte[] data = new byte[length];
			await stream.ReadAsync(data, 0, length);
			string message = Encoding.UTF8.GetString(data);

			Dispatcher.Invoke(() =>
			{
				ChatText.Text += $"\nFriend: {message}";
				StatusText.Text = "Message received";
			});
		}
	}
}