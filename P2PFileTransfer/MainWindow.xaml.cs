using System.Collections.Concurrent;
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

		private NetworkStream stream;
		private bool isRunning;
		private readonly HttpClient httpClient = new HttpClient();
		private UdpClient udpServer;
		private UdpClient udpClient;
		private IPEndPoint connectedPeer;
		private readonly ConcurrentQueue<UdpReceiveResult> packetQueue = new ConcurrentQueue<UdpReceiveResult>();

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
			stream?.Close();
			stream = null;
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


		// Central Message Dispatcher
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
					Dispatcher.Invoke(() => StatusText.Text = $"Received packet: {data[0]}");

					if (data[0] == 5)
					{
						packetQueue.Enqueue(result);
						Dispatcher.Invoke(() => StatusText.Text += " (queued for ACK)");
						continue;
					}

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
					else if (data.Length > 1 && data[0] == 3) // Message command
					{
						// We could refactor this to a method of Receiving Message
						string message = Encoding.UTF8.GetString(data, 1, data.Length - 1);
						Dispatcher.Invoke(() =>
						{
							ChatText.Text += $"\nFriend: {message}";
							StatusText.Text = "Message received";
						});
					}
					else if (data[0] == 2) // File command
					{
						await ReceiveFile(listener);
					}
					else
					{
						Dispatcher.Invoke(() =>
						{
							StatusText.Text += " (unhandled)";
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

		// Senders and Receivers
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
				UdpReceiveResult response = await udpClient.ReceiveAsync();
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
		private async void SendMessage_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (connectedPeer == null)
				{
					MessageBox.Show("Not connected!");
					return;
				}

				UdpClient udpInstance = udpServer ?? udpClient;
				if (udpInstance == null)
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

				await udpInstance.SendAsync(packet, packet.Length, connectedPeer); // Send message
				ChatText.Dispatcher.Invoke(() => ChatText.Text += $"\nYou: {message}");
				StatusText.Text = "Message sent!";
			}
			catch (Exception ex)
			{
				StatusText.Text = "Message send failed";
				MessageBox.Show($"Message error: {ex.Message}");
			}
		}
		private async void SendFile_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (connectedPeer == null || (udpServer ?? udpClient) == null)
				{
					MessageBox.Show("Not connected!");
					return;
				}

				UdpClient udpInstance = udpServer ?? udpClient;


				Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
				if (dlg.ShowDialog() != true) return;

				string filePath = dlg.FileName;
				string fileName = Path.GetFileName(filePath);
				byte[] fileData = File.ReadAllBytes(filePath); // For now, load all; we'll stream later

				StatusText.Text = $"Sending file...";

				// Send file metadata
				byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
				byte[] metaPacket = new byte[9 + nameBytes.Length];
				metaPacket[0] = 2; // File command
				BitConverter.GetBytes(nameBytes.Length).CopyTo(metaPacket, 1); // File name length
				BitConverter.GetBytes(fileData.Length).CopyTo(metaPacket, 5); // File size
				nameBytes.CopyTo(metaPacket, 9); // File name

				await SendWithRetry(udpInstance, metaPacket, connectedPeer, expectAck: true);
				StatusText.Text = "Sent metadata";

				// Send file in chunks
				const int chunkSize = 8192; // 8KB
				int totalChunks = (int)Math.Ceiling((double)fileData.Length / chunkSize); // Checking how many chunks we need
				for (int i = 0; i < totalChunks; i++)
				{
					int offset = i * chunkSize; // Sets the bytes position to start reading from, as we send, we need to keep track what bytes we've sent
					int length = Math.Min(chunkSize, fileData.Length - offset); // This is for the last chunk, if it's less than 8KB
					byte[] chunk = new byte[length + 5];
					chunk[0] = 4; // File chunk command
					BitConverter.GetBytes(i).CopyTo(chunk, 1); // Chunk index, helps receiver know the order of the chunks Ex: chunk 0, chunk 1, chunk 2...
					Array.Copy(fileData, offset, chunk, 5, length); // Copy a section of the file data to the chunk

					await SendWithRetry(udpInstance, chunk, connectedPeer);
					StatusText.Text = $"Sending file... ({i + 1}/{totalChunks} bytes)";
				}
				StatusText.Text = "File sent!";
			}
			catch (Exception ex)
			{
				StatusText.Text = "File send failed";
				MessageBox.Show($"Send error: {ex.Message}");
			}
		}
		private async Task ReceiveFile(UdpClient listener)
		{
			try
			{
				// Buffer packets until we get metadata
				List<UdpReceiveResult> packetBuffer = new List<UdpReceiveResult>();
				byte[] metaData = null;

				using (var cts = new CancellationTokenSource(10000))
				{
					while (metaData == null)
					{
						var receiveTask = listener.ReceiveAsync();
						var completedTask = await Task.WhenAny(receiveTask, Task.Delay(10000, cts.Token));

						if (completedTask == receiveTask)
						{
							UdpReceiveResult result = await receiveTask;
							Dispatcher.Invoke(() => StatusText.Text = $"Received packet: {result.Buffer[0]}");

							if (result.Buffer[0] == 2)
							{
								metaData = result.Buffer;
								byte[] ack = new byte[5];
								ack[0] = 5;
								BitConverter.GetBytes(-1).CopyTo(ack, 1);
								await listener.SendAsync(ack, ack.Length, connectedPeer);
							}
							else if (result.Buffer[0] == 4)
							{
								packetBuffer.Add(result);
							}
						}
						else
						{
							throw new TimeoutException("Metadata receive timeout");
						}
					} 
				}

				int nameLenght = BitConverter.ToInt32(metaData, 1);
				int fileSize = BitConverter.ToInt32(metaData, 5);
				string fileName = Encoding.UTF8.GetString(metaData, 9, nameLenght);

				Dispatcher.Invoke(() => StatusText.Text = $"Receiving file: {fileName}");

				// Receive file in chunks
				byte[] fileData = new byte[fileSize];
				int totalReceived = 0;
				Dictionary<int, byte[]> chunks = new Dictionary<int, byte[]>();

				// Process buffered packets
				foreach (var buffered in packetBuffer.Where(p => p.Buffer[0] == 4))
				{
					totalReceived = await ProcessChunk(buffered.Buffer, chunks, fileData, totalReceived, listener);
				}

				while (totalReceived < fileSize)
				{
					UdpReceiveResult chunkResult = await listener.ReceiveAsync();
					if (chunkResult.Buffer[0] == 4)
					{
						totalReceived = await ProcessChunk(chunkResult.Buffer, chunks, fileData, totalReceived, listener);
					}
				}

				string path = Path.Combine(Directory.GetCurrentDirectory(), $"received_{fileName}");
				File.WriteAllBytes(path, fileData);
				Dispatcher.Invoke(() => StatusText.Text = $"File received: {path}");
			}
			catch (Exception ex)
			{
				Dispatcher.Invoke(() => StatusText.Text = $"Receive file error: {ex.Message}");
			}
		}



		private async Task SendWithRetry(UdpClient udpInstance, byte[] data, IPEndPoint endpoint, int maxRetries = 3, bool expectAck = true)
		{
			for (int attempt = 0; attempt < maxRetries; attempt++)
			{
				try
				{
					// Send the data
					await udpInstance.SendAsync(data, data.Length, endpoint);
					if (!expectAck) return;	

					int seqNum = data[0] == 2 ? -1 : BitConverter.ToInt32(data, 1);
					int waitTime = 0;
					const int maxWaitTime = 5000;

					while (waitTime < maxWaitTime)
					{
						if (packetQueue.TryDequeue(out UdpReceiveResult ack))
						{
							if (ack.Buffer.Length == 5 && ack.Buffer[0] == 5 && BitConverter.ToInt32(ack.Buffer, 1) == seqNum)
							{
								Dispatcher.Invoke(() => StatusText.Text = $"Received ACK for {seqNum}");
								return;
							}
						}
						await Task.Delay(100);
						waitTime += 100;
					}
					throw new TimeoutException("No valid ACK received");
				}
				catch (Exception ex)
				{
					if (attempt == maxRetries - 1)
					{
						StatusText.Text = $"Final retry failed: {ex.Message}";
						throw;
					}
					await Task.Delay(1000 * (attempt + 1)); // Exponential backoff
				}
			}
		}
		private async Task<int> ProcessChunk(byte[] chunkData, Dictionary<int, byte[]> chunks, byte[] fileData, int totalReceived, UdpClient listener)
		{
			int seqNum = BitConverter.ToInt32(chunkData, 1);
			int chunkLength = chunkData.Length - 5;

			if (!chunks.ContainsKey(seqNum))
			{
				byte[] chunkContent = new byte[chunkLength];
				Array.Copy(chunkData, 5, chunkContent, 0, chunkLength);
				chunks[seqNum] = chunkContent;

				int position = seqNum * 8192;
				Array.Copy(chunkContent, 0, fileData, position, chunkLength);
				totalReceived += chunkLength;

				byte[] ack = new byte[5];
				ack[0] = 5;
				BitConverter.GetBytes(seqNum).CopyTo(ack, 1);
				await listener.SendAsync(ack, ack.Length, connectedPeer);

				Dispatcher.Invoke(() => StatusText.Text = $"Sent ACK for chunk {seqNum}, total: {totalReceived}/{fileData.Length}");
			}
			return totalReceived;
		}
	}
}