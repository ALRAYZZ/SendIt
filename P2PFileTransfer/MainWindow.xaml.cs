using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;


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
		private readonly string rendezvousServer = "http://localhost:5001";
		private readonly ConcurrentQueue<UdpReceiveResult> packetQueue = new ConcurrentQueue<UdpReceiveResult>();

		public MainWindow()
		{
			InitializeComponent();
		}
		private async void GetIp_Click(object sender, RoutedEventArgs e)
		{
			string endpoint = await GetPublicIp();
			MessageBox.Show($"Your public IP:Port is {endpoint}");
		}
		private async Task<string> GetPublicIp()
		{
			try
			{
				string ip = await httpClient.GetStringAsync("https://api.ipify.org");
				int port = udpClient?.Client.LocalEndPoint is IPEndPoint ep ? ep.Port : 5000; // Use bound port or default 5000
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
					MessageBox.Show("Enter rendezvous server IP:Port");
					return;
				}
				// Parse rendezvous server address from input
				string[] parts = FriendAddress.Text.Split(':');
				string rendezvousIp = parts[0];
				int rendezvousPort = int.Parse(parts[1]);
				IPEndPoint rendezvousEndpoint = new IPEndPoint(IPAddress.Parse(rendezvousIp), rendezvousPort);
				
				// Get role from ComboBox
				string? role = (PeerRole.SelectedItem as ComboBoxItem)?.Content.ToString();
				if (string.IsNullOrEmpty(role))
				{
					MessageBox.Show("Select a role");
					return;
				}
				int localPort = role == "PEER1" ? 5000 : 5002; // PEER1 listens on 5000, PEER2 on 5002

				// Bind udpClient to a fixed port
				udpClient = new UdpClient(localPort);
				isRunning = true;


				byte[] roleBytes = Encoding.UTF8.GetBytes(role);
				await udpClient.SendAsync(roleBytes, roleBytes.Length, rendezvousEndpoint); // Send role to server
				StatusText.Text = $"Sent role: {role}";



				// Get peer's IP:Port
				UdpReceiveResult response = await udpClient.ReceiveAsync();
				string peerEndpointStr = Encoding.UTF8.GetString(response.Buffer);
				string[] peerParts = peerEndpointStr.Split(':');
				connectedPeer = new IPEndPoint(IPAddress.Parse(peerParts[0]), int.Parse(peerParts[1]));
				StatusText.Text = $"Got peer endpoint: {connectedPeer}";

				// Punch a hole by sending initial packets
				byte[] punchPacket = Encoding.UTF8.GetBytes("PUNCH");
				for (int i = 0; i < 10; i++) // Send multiple to ensure NAT opens
				{
					await udpClient.SendAsync(punchPacket, punchPacket.Length, connectedPeer);
					await Task.Delay(50); // Spread out to open NAT
				}

				StatusText.Text = $"Connected to {connectedPeer}";
				MessageBox.Show("Connected!");
				_ = ListenForUdpMessages(udpClient); // Start listening for messages
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

				using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
				{
					// Send file metadata
					long fileSize = fs.Length;
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
						byte[] chunkContent = new byte[length + 5];
						await fs.ReadAsync(chunkContent, 0, length); // Read chunk from disk

						byte[] chunk = new byte[length + 5];
						chunk[0] = 4; // Chunk command
						BitConverter.GetBytes(i).CopyTo(chunk, 1); // Chunk index, helps receiver know the order of the chunks Ex: chunk 0, chunk 1, chunk 2...
						Array.Copy(chunkContent, 0, chunk, 5, length);

						await SendWithRetry(udpInstance, chunk, connectedPeer);
						StatusText.Text = $"Sending file... ({i + 1}/{totalChunks} bytes)";
					}
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
				long fileSize = BitConverter.ToInt32(metaData, 5);
				string fileName = Encoding.UTF8.GetString(metaData, 9, nameLenght);
				string path = Path.Combine(Directory.GetCurrentDirectory(), $"received_{fileName}");

				Dispatcher.Invoke(() => StatusText.Text = $"Receiving file: {fileName}");

				// Write chunks to disk as they arrive
				using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
				{
					Dictionary<int, byte[]> chunks = new Dictionary<int, byte[]>();
					long totalReceived = 0;

					// Process buffered chunks
					foreach (var buffered in packetBuffer.Where(p => p.Buffer[0] == 4))
					{
						totalReceived = await ProcessChunk(buffered.Buffer, chunks, fs, totalReceived, listener);
					}

					while (totalReceived < fileSize)
					{
						UdpReceiveResult chunkResult = await listener.ReceiveAsync();
						if (chunkResult.Buffer[0] == 4)
						{
							totalReceived = await ProcessChunk(chunkResult.Buffer, chunks, fs, totalReceived, listener);
						}
					} 
				}
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
		private async Task<long> ProcessChunk(byte[] chunkData, Dictionary<int, byte[]> chunks, FileStream fs, long totalReceived, UdpClient listener)
		{
			int seqNum = BitConverter.ToInt32(chunkData, 1);
			int chunkLength = chunkData.Length - 5;

			if (!chunks.ContainsKey(seqNum))
			{
				byte[] chunkContent = new byte[chunkLength];
				Array.Copy(chunkData, 5, chunkContent, 0, chunkLength);
				chunks[seqNum] = chunkContent;

				long position = seqNum * 8192;
				fs.Seek(position, SeekOrigin.Begin);
				await fs.WriteAsync(chunkContent, 0, chunkLength);
				totalReceived += chunkLength;

				byte[] ack = new byte[5];
				ack[0] = 5;
				BitConverter.GetBytes(seqNum).CopyTo(ack, 1);
				await listener.SendAsync(ack, ack.Length, connectedPeer);

				Dispatcher.Invoke(() => StatusText.Text = $"Sent ACK for chunk {seqNum}, total: {totalReceived}/{fs.Length}");
			}
			return totalReceived;
		}
	}
}