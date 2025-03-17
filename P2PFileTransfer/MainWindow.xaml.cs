using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Microsoft.VisualBasic;

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
		private bool isServerRunning; // To check if the server is running
		private const int CHUNK_SIZE = 1024 * 1024; // 1MB chunks
		private const int BUFFER_SIZE = 4096; // 4KB buffer for network transfer
		private byte[]? aesKey;
		private byte[]? aesIv;

		
		public MainWindow()
        {
            InitializeComponent();
        }

        private void btnGenerateId_Click(object sender, RoutedEventArgs e)
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
		private async void btnStartServer_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (isServerRunning)
				{
					MessageBox.Show("Server is already running!");
					return;
				}
				// Set up the server to listen on any IP (0.0.0.0) and port 5000
				server = new TcpListener(IPAddress.Any, 5000);
				server.Start();
				isServerRunning = true;
				txtStatus.Text = "Server started. Waiting for connections...";
				MessageBox.Show("Server started!");

				// Keep accepting clients in a loop
				await Task.Run(async () =>
				{
					while (isServerRunning)
					{
						try
						{
							TcpClient client = await server.AcceptTcpClientAsync();
							txtStatus.Dispatcher.Invoke(() => txtStatus.Text = "Status: Connected");

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
		private async Task HandleClientAsync(TcpClient client)
		{
			try
			{
				NetworkStream stream = client.GetStream();

				while (true) // Keep connection alive for multiple commands
				{
					// Wait for a command
					byte[] commandBuffer = new byte[1];
					int bytesRead = await stream.ReadAsync(commandBuffer, 0, 1);
					if (bytesRead == 0) break; // Client disconnected

					byte command = commandBuffer[0];
					if (command == 1) // File send command
					{
						// Exchange encryption keys
						await ExchangeEncryptionKeys(stream, true);

						// Receive file name length and name
						byte[] nameLengthBytes = new byte[4];
						await stream.ReadAsync(nameLengthBytes, 0, 4);
						int nameLength = BitConverter.ToInt32(nameLengthBytes, 0);

						byte[] nameBytes = new byte[nameLength];
						await stream.ReadAsync(nameBytes, 0, nameLength);
						string fileName = Encoding.UTF8.GetString(nameBytes);

						// Receive and decrypt file
						string outputPath = Path.Combine(Directory.GetCurrentDirectory(), $"received_{fileName}");
						using (Aes aes = Aes.Create())
						{
							aes.Key = aesKey!;
							aes.IV = aesIv!;
							using (FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
							using (CryptoStream cryptoStream = new CryptoStream(fileStream, aes.CreateDecryptor(), CryptoStreamMode.Write))
							{
								byte[] buffer = new byte[BUFFER_SIZE];
								while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
								{
									await cryptoStream.WriteAsync(buffer, 0, bytesRead);
								}
							}
						}
						txtStatus.Dispatcher.Invoke(() => txtStatus.Text = "Status: File received");
					}
					else if (command == 0) // Connect only command
					{
						txtStatus.Dispatcher.Invoke(() => txtStatus.Text = "Status: Client connected, no file sent");
					}
					else
					{
						txtStatus.Dispatcher.Invoke(() => txtStatus.Text = "Status: Unknown command");
					}
				}

				client.Close();
			}
			catch (Exception ex)
			{
				txtStatus.Dispatcher.Invoke(() => txtStatus.Text = "Status: Error receiving file");
				MessageBox.Show($"Receive error: {ex.Message}");
				client.Close();
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
		private async void btnConnect_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				connectedClient = await ConnectToFriendAsync();
				NetworkStream stream = connectedClient.GetStream();
				await stream.WriteAsync(new byte[] { 0 }, 0, 1); // Send a "connect only" command
				MessageBox.Show("Connected to friend!");
				// Dont close the client here, it will be closed after file transfer

			}
			catch (Exception ex)
			{
				txtStatus.Text = "Status: Error";
				MessageBox.Show($"Error connecting to friend: {ex.Message}");
				connectedClient?.Close();
				connectedClient = null;
			}
		}
		private async void btnSendFile_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				OpenFileDialog openFileDialog = new OpenFileDialog();
				if (openFileDialog.ShowDialog() != true) return;

				string filePath = openFileDialog.FileName;
				string fileName = Path.GetFileName(filePath);

				TcpClient client = connectedClient ?? await ConnectToFriendAsync(); // Use existing connection or create a new one
				txtStatus.Text = "Status: Connected, sending file...";

				NetworkStream stream = client.GetStream();

				// Send a "send file" command
				await stream.WriteAsync(new byte[] { 1 }, 0, 1);

				// Exchange encryption keys
				await ExchangeEncryptionKeys(stream, false);

				// Send file name length and name
				byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
				byte[] nameLengthBytes = BitConverter.GetBytes(nameBytes.Length);
				await stream.WriteAsync(nameLengthBytes, 0, 4);
				await stream.WriteAsync(nameBytes, 0, nameBytes.Length);

				// Send encrypted file in chunks
				using (Aes aes = Aes.Create())
				{
					aes.Key = aesKey!;
					aes.IV = aesIv!;

					using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
					using (CryptoStream cryptoStream = new CryptoStream(stream, aes.CreateEncryptor(), CryptoStreamMode.Write))
					{
						byte[] buffer = new byte[BUFFER_SIZE];
						int bytesRead;
						while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
						{
							await cryptoStream.WriteAsync(buffer, 0, bytesRead);
						}
						await cryptoStream.FlushFinalBlockAsync(); // Ensure all encrypted data is sent
					} 
				}

				txtStatus.Text = "Status: File sent";

			}
			catch (Exception ex)
			{
				txtStatus.Text = "Status: Send failed";
				MessageBox.Show($"Error sending file: {ex.Message}");
				connectedClient?.Close();
				connectedClient = null;
			}
		}
		private async Task ExchangeEncryptionKeys(NetworkStream stream, bool isServer)
		{
			if (isServer)
			{
				// Server: Receive encrypted AES key and IV
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
				aesKey = rsa!.Decrypt(encryptedKey, true);
				aesIv = rsa!.Decrypt(encryptedIv, true);
			}
			else
			{
				// Client: Generate AES key and IV
				using (Aes aes = Aes.Create())
				{
					aesKey = aes.Key;
					aesIv = aes.IV;

					RSACryptoServiceProvider recipientRsa = new RSACryptoServiceProvider();
					recipientRsa.FromXmlString(txtFriendId.Text);

					// Encrypt the AES key and IV using RSA
					byte[] encryptedKey = recipientRsa.Encrypt(aesKey, true);
					byte[] keyLengthBytes = BitConverter.GetBytes(encryptedKey.Length);
					await stream.WriteAsync(keyLengthBytes, 0, 4);
					await stream.WriteAsync(encryptedKey, 0, encryptedKey.Length);

					byte[] encryptedIv = recipientRsa.Encrypt(aesIv, true);
					byte[] ivLengthBytes = BitConverter.GetBytes(encryptedIv.Length);
					await stream.WriteAsync(ivLengthBytes, 0, 4);
					await stream.WriteAsync(encryptedIv, 0, encryptedIv.Length);
				}
			}
		}
	}
}