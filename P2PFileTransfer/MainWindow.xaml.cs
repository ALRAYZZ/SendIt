using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Windows;


namespace SendIt
{
	public partial class MainWindow : Window
	{
		private TcpListener server;
		private TcpClient client;
		private NetworkStream stream;
		private bool isRunning;
		private readonly HttpClient httpClient = new HttpClient();
		private UdpClient udpServer;
		private UdpClient udpClient;
		private IPEndPoint connectedPeer;

		public MainWindow()
		{
			InitializeComponent();
		}
		private async void StartServer_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (isRunning)
				{
					MessageBox.Show("Server already running!");
					return;
				}

				string endpoint = await GetPublicIp();
				int port = 5000;
				udpServer = new UdpClient(port);
				isRunning = true;
				StatusText.Text = $"Server started, Your ID: {endpoint}";

				await ListenForUdpMessages(udpServer); // Start listenting
			}
			catch (Exception ex)
			{
				StatusText.Text = "Error starting server";
				MessageBox.Show($"Server error: {ex.Message}");
				ShutdownServer();
			}
		}
		private async Task ListenForUdpMessages(UdpClient listener) // Both server and client can listen for messages
		{
			try
			{
				while (isRunning)
				{
					// Server listens for incoming messages
					UdpReceiveResult result = await listener.ReceiveAsync(); // Wait for message
					byte[] data = result.Buffer; // Get message data
					IPEndPoint remoteEndPoint = result.RemoteEndPoint; // Get sender's IP:Port

					if (listener == udpServer && data.Length == 1 && data[0] == 1) // Connection request value as defined in Connect_Click only for server
					{
						var accept = MessageBox.Show($"Accept connection from {remoteEndPoint}?",
							"Connection Request", MessageBoxButton.YesNo);
						byte[] response = new byte[] { (byte)(accept == MessageBoxResult.Yes ? 2 : 0) }; // If yes, send 2, else 0
						await udpServer.SendAsync(response, response.Length, remoteEndPoint); // Send response

						if (accept == MessageBoxResult.Yes)
						{
							// Save the connected peer for identifying the sender
							connectedPeer = remoteEndPoint;
							Dispatcher.Invoke(() =>
							{
								StatusText.Text = $"Connected to {remoteEndPoint}";
							});
							// TODO: Save remoteEndPoint for sending later
						}
					}
					else if (data.Length > 1 && data[0] == 3)
					{
						string message = Encoding.UTF8.GetString(data, 1, data.Length - 1);
						Dispatcher.Invoke(() =>
						{
							ChatText.Text += $"\nFriend: {message}";
							StatusText.Text = "Message received";
						});
					}

				}
			}
			catch (Exception ex)
			{
				Dispatcher.Invoke(() =>
				{
					StatusText.Text = $"Listening stopped: " + ex.Message;
					ShutdownServer();
				});
			}
		}
		private void StopServer_Click(object sender, RoutedEventArgs e)
		{
			ShutdownServer();
			StatusText.Text = "Server stopped";
			MessageBox.Show("Server stopped");
		}
		private async void GetIp_Click(object sender, RoutedEventArgs e)
		{
			string endpoint = await GetPublicIp();
			MessageBox.Show($"Your public IP:Port is {endpoint}");
		}
		private void ShutdownServer()
		{
			isRunning = false;
			udpServer?.Close();
			udpServer = null;


			server?.Stop();
			client?.Close();
			stream?.Close();
			client = null;
			stream = null;
		}
		private async void Connect_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (string.IsNullOrEmpty(FriendAddress.Text))
				{
					MessageBox.Show("Enter friend's IP:Port");
					return;
				}

				// Parse the friend's IP and port from the text box
				string[] parts = FriendAddress.Text.Split(':');
				string ip = parts[0];
				int port = int.Parse(parts[1]);
				
				udpClient = new UdpClient();
				connectedPeer = new IPEndPoint(IPAddress.Parse(ip), port);

				// Client sends a test packet to the friend when connecting
				StatusText.Text = $"Connecting to {FriendAddress.Text}...";
				byte[] testPacket = new byte[] { 1 }; // Connection request set by "1"
				await udpClient.SendAsync(testPacket, testPacket.Length, connectedPeer); // We can also put "0" on the 2nd parameter, meanign we send the whole packet

				// Client starts listening after sending the connection request to enable bidirectional communication
				UdpReceiveResult response =  await udpClient.ReceiveAsync();
				if (response.Buffer[0] == 2) // 2 == Request accepted by peer
				{
					isRunning = true;
					StatusText.Text = $"Connected to {FriendAddress.Text}!";
					MessageBox.Show("Connected!");
					await ListenForUdpMessages(udpClient); // Start listening for messages
				}
				else
				{
					throw new Exception("Connection refused");
				}
			}
			catch (Exception ex)
			{
				StatusText.Text = "Connection failed";
				MessageBox.Show($"Connection error: {ex.Message}");
				udpClient?.Close();
				udpClient = null;
				connectedPeer = null;
			}
		}
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
		private async void SendMessage_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (connectedPeer == null)
				{
					MessageBox.Show("Not connected!");
					return;
				}

				UdpClient udpConnection = udpServer ?? udpClient;
				if (udpConnection == null)
				{
					MessageBox.Show("Not connected!");
					return;
				}

				string message = Microsoft.VisualBasic.Interaction.InputBox("Enter message:", "Send Message");
				if (string.IsNullOrEmpty(message)) return;

				byte[] messageBytes = Encoding.UTF8.GetBytes(message); // Convert message to bytes
				byte[] packet = new byte[messageBytes.Length + 1]; // Create packet with message length + 1 for command
				packet[0] = 3; // Message command
				Array.Copy(messageBytes, 0, packet, 1, messageBytes.Length); // Combine command and message

				await udpConnection.SendAsync(packet, packet.Length, connectedPeer); // Send message
				ChatText.Dispatcher.Invoke(() => ChatText.Text += $"\nYou: {message}");
				StatusText.Text = "Message sent!";
			}
			catch (Exception ex)
			{
				StatusText.Text = "Message send failed";
				MessageBox.Show($"Message error: {ex.Message}");
			}
		}
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
		private async Task<string> GetPublicIp()
		{
			try
			{
				string ip = await httpClient.GetStringAsync("https://api.ipify.org");
				int port = 5000;
				return $"{ip}:{port}";	
			}
			catch (Exception ex)
			{
				throw new Exception($"Failed to get public IP: {ex.Message}");
			}

		}
	}
}