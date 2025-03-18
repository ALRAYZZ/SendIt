using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Microsoft.VisualBasic;
using System.Diagnostics.Eventing.Reader;

namespace P2PFileTransfer
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
    {
		private RSACryptoServiceProvider? rsa; // To store the RSA key pair
		private TcpListener? server; // To listen for incoming connections
		private TcpClient? connectedClient; // To store the connected client
		private List<Friend> friends = new List<Friend>(); // To store the connected friends
		private bool isServerRunning; // To check if the server is running
		private const int CHUNK_SIZE = 1024 * 1024; // 1MB chunks
		private const int BUFFER_SIZE = 4096; // 4KB buffer for network transfer

		private class Friend
		{
			public string IpPort { get; set; }
			public string PublicKey { get; set; }
			public TcpClient Client { get; set; }
			public byte[] AesKey { get; set; }
			public byte[] AesIv { get; set; }
			public Friend(string ipPort, string publicKey, TcpClient client, byte[] aesKey, byte[] aesIv)
			{
				IpPort = ipPort;
				PublicKey = publicKey;
				Client = client;
				AesKey = aesKey;
				AesIv = aesIv;
			}
		}

		public MainWindow()
        {
            InitializeComponent();
        }

		private void btnGenerateId_Click(object sender, RoutedEventArgs e) => GenerateId();
		private void btnStartServer_Click(object sender, RoutedEventArgs e) => StartServer();
		private void btnStopServer_Click(object sender, RoutedEventArgs e) => StopServer();
		private void btnConnect_Click(object sender, RoutedEventArgs e) => ConnectToFriend();
		private void btnSendFile_Click(object sender, RoutedEventArgs e) => SendFile();
		private void btnSendMessage_Click(object sender, RoutedEventArgs e) => SendMessage();
		private void btnDisconnect_Click(object sender, RoutedEventArgs e) => Disconnect();

		private void GenerateId()
		{
			try
			{
				// Create a new RSA key pai with 2048 bits
				rsa = new RSACryptoServiceProvider(2048);
				// Get the public key as string
				string publicKey = rsa.ToXmlString(false); // false = public key
				// Display the public key in the textbox
				txtYourId.Text = publicKey;
				MessageBox.Show("ID Generated! Share this with your friends to connect with them.");

			}
			catch (Exception ex)
			{ 
				MessageBox.Show($"Error generatin ID: {ex.Message}");
			}
		}
		private async void StartServer()
		{
			try
			{
				if (isServerRunning)
				{
					MessageBox.Show("Server is running!");
					return;
				}
				// Set up the server to listen on any IP (0.0.0.0) and port 5000
				server = new TcpListener(IPAddress.Any, 5000);
				server.Start();
				isServerRunning = true;
				txtStatus.Text = "Server started. Waiting for connections...";

				// Keep accepting clients in a loop
				await Task.Run(async () =>
				{
					while (isServerRunning)
					{
						try
						{
							TcpClient client = await server.AcceptTcpClientAsync();
							txtStatus.Dispatcher.Invoke(() => txtStatus.Text = "Client Connected");
							// Handle the client in a separate task
							_ = Task.Run(() => HandleClientAsync(client));
						}
						catch (Exception ex)
						{
							txtStatus.Dispatcher.Invoke(() => txtStatus.Text = "Status: Error");
							MessageBox.Show($"Error accepting client: {ex.Message}");
						}
					}
				});
			}
			catch (Exception ex)
			{
				txtStatus.Text = "Status: Error";
				MessageBox.Show($"Error starting server: {ex.Message}");
				server?.Stop(); // Stop the server if it was started
				isServerRunning = false;
			}
		}
		private void StopServer()
		{
			try
			{
				if (!isServerRunning)
				{
					MessageBox.Show("Server is not running!");
					return;
				}
				isServerRunning = false;
				server?.Stop();
				foreach (var friend in friends)
				{
					friend.Client.Close();
				}
				friends.Clear();
				connectedClient?.Close();
				connectedClient = null;
				cbFriends.Dispatcher.Invoke(() => cbFriends.Items.Clear());
				txtStatus.Text = "Server stopped";
				MessageBox.Show("Server stopped");
			}
			catch (Exception ex)
			{
				txtStatus.Text = "Error";
				MessageBox.Show($"Stop server: {ex.Message}");
			}
		}
		private async Task HandleClientAsync(TcpClient client)
		{
			try
			{
				NetworkStream stream = client.GetStream();
				string ipPort = ((IPEndPoint)client.Client.RemoteEndPoint).ToString();


				// Handle initial connection request
				byte[] cmd = new byte[1];
				int read = await stream.ReadAsync(cmd, 0, 1);
				if (read == 0 || cmd[0] != 0)  // Client disconnected
				{
					client.Close();
					return;
				}

				// Receive encryption keys
				byte[] keyLengthBytes = new byte[4];
				await stream.ReadAsync(keyLengthBytes, 0, 4);
				int keyLength = BitConverter.ToInt32(keyLengthBytes, 0);
				byte[] encryptedKey = new byte[keyLength];
				await stream.ReadAsync(encryptedKey, 0, keyLength);

				byte[] ivLengthBytes = new byte[4];
				await stream.ReadAsync(ivLengthBytes, 0, 4);
				int ivLength = BitConverter.ToInt32(ivLengthBytes, 0);
				byte[] encryptedIv = new byte[ivLength];
				await stream.ReadAsync(encryptedIv, 0, ivLength);

				// Decrypt the AES key and IV using RSA
				byte[] aesKey = rsa!.Decrypt(encryptedKey, true);
				byte[] aesIv = rsa!.Decrypt(encryptedIv, true);

				var result = MessageBox.Show($"Accept connection from {ipPort}?", "Connection Request", MessageBoxButton.YesNo);
				await stream.WriteAsync(new byte[] { (byte)(result == MessageBoxResult.Yes ? 1 : 0) }, 0, 1);
				if (result == MessageBoxResult.No)
				{
					client.Close();
					return;
				}

				// Connection accepted
				txtStatus.Dispatcher.Invoke(() => txtStatus.Text = $"Client connected: {ipPort}");
				friends.Add(new Friend(ipPort, "Unkown", client, aesKey, aesIv));
				cbFriends.Dispatcher.Invoke(() =>
				{
					cbFriends.Items.Add(ipPort);
				});

				// Start listening for messages and other commands
				while (client.Connected)
				{
					read = await stream.ReadAsync(cmd, 0, 1);
					if (read == 0)
					{
						break;
					}
					if (cmd[0] == 1)
					{
						string fileName = await ReceiveFileName(stream);
						var fileResult = MessageBox.Show($"Accept file {fileName} from {ipPort}?", "File Request", MessageBoxButton.YesNo);
						await stream.WriteAsync(new byte[] { (byte)(fileResult == MessageBoxResult.Yes ? 1 : 0) }, 0, 1);
						if (fileResult == MessageBoxResult.Yes)
						{
							byte[] sizeBytes = new byte[8];
							await stream.ReadAsync(sizeBytes, 0, 8);
							long fileSize = BitConverter.ToInt64(sizeBytes, 0);
							pbProgress.Dispatcher.Invoke(() => pbProgress.Maximum = fileSize);

							string path = Path.Combine(Directory.GetCurrentDirectory(), $"received_{fileName}");
							await ReceiveEncryptedFile(stream, path, aesKey, aesIv);
							txtStatus.Dispatcher.Invoke(() => txtStatus.Text = $"File received: received_{fileName}");
							pbProgress.Dispatcher.Invoke(() => pbProgress.Value = 0);
						}
					}
					else if (cmd[0] == 3) // Message
					{
						string message = await ReceiveFileName(stream); // Reusing method for simplicity
						txtChat.Dispatcher.Invoke(() => txtChat.Text += $"\n{ipPort}: {message}");
					}
				}
				client.Close();
			}
			catch (Exception ex)
			{
				txtStatus.Dispatcher.Invoke(() => txtStatus.Text = "Error receiving");
				MessageBox.Show($"Receive error: {ex.Message}");
				client.Close();
			}
		}
		private async Task ListenForMessagesAsync(TcpClient client, string ipPort)
		{
			try
			{
				NetworkStream stream = client.GetStream();
				while (client.Connected)
				{
					byte[] cmd = new byte[1];
					int read = await stream.ReadAsync(cmd, 0, 1);
					if (read == 0) break; // Client disconnected

					if (cmd[0] == 3)
					{
						string message = await ReceiveFileName(stream); // Reusing method for simplicity
						txtChat.Dispatcher.Invoke(() => txtChat.Text += $"\n{ipPort}: {message}");
					}
				}
			}
			catch (Exception ex)
			{
				txtStatus.Dispatcher.Invoke(() => txtStatus.Text = "Messaging error");
				MessageBox.Show($"Messaging error: {ipPort}: {ex.Message}");
			}
		}
		private async Task<string> ReceiveFileName(NetworkStream stream)
		{
			byte[] lenBytes = new byte[4];
			await stream.ReadAsync(lenBytes, 0, 4);
			int len = BitConverter.ToInt32(lenBytes, 0);
			byte[] nameBytes = new byte[len];
			await stream.ReadAsync(nameBytes, 0, len);
			return Encoding.UTF8.GetString(nameBytes);
		}
		private async Task ReceiveEncryptedFile(NetworkStream stream, string path, byte[]aesKey, byte[]aesIv)
		{
			using (Aes aes = Aes.Create())
			{
				aes.Key = aesKey;
				aes.IV = aesIv;
				using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
				using (CryptoStream cs = new CryptoStream(fs, aes.CreateDecryptor(), CryptoStreamMode.Write))
				{
					byte[] buffer = new byte[BUFFER_SIZE];
					int bytesRead;
					long totalBytesReceived = 0;
					while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
					{
						await cs.WriteAsync(buffer, 0, bytesRead);
						totalBytesReceived += bytesRead;
						pbProgress.Dispatcher.Invoke(() => pbProgress.Value = totalBytesReceived);
					}
				}
			}
		}
		private async Task<TcpClient> ConnectToFriendAsync()
		{
			string[] addressParts = txtFriendAddress.Text.Split(':');
			if (addressParts.Length != 2 || !int.TryParse(addressParts[1], out int port))
			{
				throw new Exception("Invalid format. Use IP:Port");
			}
			string ip = addressParts[0];

			TcpClient client = new TcpClient();
			txtStatus.Text = "Status: Connecting...";
			await client.ConnectAsync(ip, port);
			txtStatus.Text = "Status: Connected";
			return client;
		}
		private async void ConnectToFriend()
		{
			try
			{
				connectedClient = await ConnectToFriendAsync();
				NetworkStream stream = connectedClient.GetStream();
				await stream.WriteAsync(new byte[] { 0 }, 0, 1); // Send a "connect only" command

				// Send AES key and IV encrypted with friend's public key
				byte[] aesKey, aesIv;
				using (Aes aes = Aes.Create())
				{
					aesKey = aes.Key;
					aesIv = aes.IV;
					RSACryptoServiceProvider recipientRsa = new RSACryptoServiceProvider();
					recipientRsa.FromXmlString(txtFriendId.Text);

					byte[] encryptedKey = recipientRsa.Encrypt(aesKey, true);
					byte[] keyLengthBytes = BitConverter.GetBytes(encryptedKey.Length);
					await stream.WriteAsync(keyLengthBytes, 0, 4);
					await stream.WriteAsync(encryptedKey, 0, encryptedKey.Length);

					byte[] encryptedIv = recipientRsa.Encrypt(aesIv, true);
					byte[] ivLengthBytes = BitConverter.GetBytes(encryptedIv.Length);
					await stream.WriteAsync(ivLengthBytes, 0, 4);
					await stream.WriteAsync(encryptedIv, 0, encryptedIv.Length);
				}

				// Wait for connection request response
				byte[] response = new byte[1];
				int read = await stream.ReadAsync(response, 0,1);
				if (read == 0 || response[0] == 0) // 0 = Connection denied
				{
					throw new Exception("Connection request denied by friend.");
				}

				// Connection accepted
				string ipPort = txtFriendAddress.Text;
				friends.Add(new Friend(ipPort, txtFriendId.Text, connectedClient, aesKey, aesIv));
				cbFriends.Dispatcher.Invoke(() =>
				{
					cbFriends.Items.Add(ipPort);
					cbFriends.SelectedIndex = cbFriends.Items.Count - 1;
				});
				txtStatus.Text = $"Status: Connected to {ipPort}";
				MessageBox.Show($"Connected to {ipPort}!");
				// Dont close the client here, it will be closed after file transfer

				_ = Task.Run(() => ListenForMessagesAsync(connectedClient, ipPort));

			}
			catch (Exception ex)
			{
				txtStatus.Text = "Status: Error";
				MessageBox.Show($"Error connecting to friend: {ex.Message}");
				connectedClient?.Close();
				connectedClient = null;
			}
		}
		private async void SendFile()
		{
			try
			{
				if (cbFriends.SelectedIndex == -1)
				{
					MessageBox.Show("Select a friend to send the file to.");
					return;
				}

				OpenFileDialog openFileDialog = new OpenFileDialog();
				if (openFileDialog.ShowDialog() != true) return;

				string filePath = openFileDialog.FileName;
				string fileName = Path.GetFileName(filePath);
				long fileSize = new FileInfo(filePath).Length;
				long totalBytesSent = 0;

				Friend friend = friends[cbFriends.SelectedIndex];
				if (friend.Client != connectedClient || !connectedClient.Connected)
				{
					connectedClient= friend.Client;
					if (!connectedClient.Connected)
					{
						connectedClient = await ConnectToFriendAsync();
					}
				}

				txtStatus.Text = "Sending file...";
				NetworkStream stream = connectedClient.GetStream();

				// Send a "send file" command
				await stream.WriteAsync(new byte[] { 1 }, 0, 1);

				// Send file name length and name
				byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
				byte[] nameLengthBytes = BitConverter.GetBytes(nameBytes.Length);
				await stream.WriteAsync(nameLengthBytes, 0, 4);
				await stream.WriteAsync(nameBytes, 0, nameBytes.Length);

				byte[] response = new byte[1];
				int read = await stream.ReadAsync(response, 0, 1);
				if (read == 0 || response[0] == 0) // 0 = File send denied
				{
					txtStatus.Text = "File send denied by friend.";
					MessageBox.Show("Friend denied file send request.");
					return;
				}

				// Send file size and proceed with transfer
				byte[] sizeBytes = BitConverter.GetBytes(fileSize);
				await stream.WriteAsync(sizeBytes, 0, 8); // Send file size

				// Send encrypted file in chunks
				using (Aes aes = Aes.Create())
				{
					aes.Key = friend.AesKey;
					aes.IV = friend.AesIv;

					using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
					using (CryptoStream cryptoStream = new CryptoStream(stream, aes.CreateEncryptor(), CryptoStreamMode.Write))
					{
						byte[] buffer = new byte[BUFFER_SIZE];
						int bytesRead;
						pbProgress.Dispatcher.Invoke(() => pbProgress.Maximum = fileSize);
						while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
						{
							await cryptoStream.WriteAsync(buffer, 0, bytesRead);
							totalBytesSent += bytesRead;
							pbProgress.Dispatcher.Invoke(() => pbProgress.Value = totalBytesSent);
						}
						await cryptoStream.FlushFinalBlockAsync(); // Ensure all encrypted data is sent
					} 
				}

				txtStatus.Text = "File sent!";
				pbProgress.Dispatcher.Invoke(() => pbProgress.Value = 0);

			}
			catch (Exception ex)
			{
				txtStatus.Text = "Status: Send failed";
				MessageBox.Show($"Error sending file: {ex.Message}");
				connectedClient?.Close();
				connectedClient = null;
			}
		}
		private async void SendMessage()
		{
			try
			{
				if (cbFriends.SelectedIndex == -1)
				{
					MessageBox.Show("Select a frriend to send a message to");
					return;
				}

				string message = Microsoft.VisualBasic.Interaction.InputBox("Enter message:", "Send Message");
				if (string.IsNullOrEmpty(message)) return;
				
				Friend friend = friends[cbFriends.SelectedIndex];
				if (!friend.Client.Connected)
				{
					txtStatus.Text = "Friend disconnected.";
					MessageBox.Show("Friend disconnected.");
					return;
				}

				txtStatus.Text = "Sending message...";
				NetworkStream stream = friend.Client.GetStream();

				// Send a "send message" command
				await stream.WriteAsync(new byte[] { 3 }, 0, 1);

				// Send message length and message
				byte[] messageBytes = Encoding.UTF8.GetBytes(message);
				byte[] messageLengthBytes = BitConverter.GetBytes(messageBytes.Length);
				await stream.WriteAsync(messageLengthBytes, 0, 4);
				await stream.WriteAsync(messageBytes, 0, messageBytes.Length);

				txtChat.Dispatcher.Invoke(() => txtChat.Text += $"\nYou: {message}");
				txtStatus.Text = "Message sent!";
			}
			catch (Exception ex)
			{
				txtStatus.Text = "Status: Message send failed!";
				MessageBox.Show($"Error sending message: {ex.Message}");
			}
		}
		private void Disconnect()
		{
			try
			{
				if (cbFriends.SelectedIndex == -1)
				{
					MessageBox.Show("Select a friend to disconnect.");
					return;
				}
				Friend friend = friends[cbFriends.SelectedIndex];
				friend.Client.Close();
				if (friend.Client == connectedClient)
				{
					connectedClient = null;
				}
				friends.RemoveAt(cbFriends.SelectedIndex);
				cbFriends.Dispatcher.Invoke(() =>
				{
					cbFriends.Items.RemoveAt(cbFriends.SelectedIndex);
					cbFriends.SelectedIndex = friends.Count > 0 ? 0 : -1;
				});
				txtStatus.Text = "Disconnected";
				MessageBox.Show($"Disconnected from {friend.IpPort}");
			}
			catch (Exception ex)
			{
				txtStatus.Text = "Error";
				MessageBox.Show($"Disconnect error: {ex.Message}");
			}
		}
	}
}