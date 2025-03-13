using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Markup;

namespace P2PFileTransfer
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
    {
		private RSACryptoServiceProvider rsa; // To store the RSA key pair
		private TcpListener server; // To listen for incoming connections
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
				// Set up the server to listen on any IP (0.0.0.0) and port 5000
				server = new TcpListener(IPAddress.Any, 5000);
				server.Start();
				txtStatus.Text = "Server started. Waiting for connections...";

				// Wait for a client to connect (async so UI does not freeze)
				TcpClient client = await Task.Run(() => server.AcceptTcpClient());
				txtStatus.Text = "Status: Connected";

				client.Close();
				server.Stop();
				txtStatus.Text = "Status: Server stopped";
			}
			catch (Exception ex)
			{
				txtStatus.Text = "Status: Error";
				MessageBox.Show($"Error starting server: {ex.Message}");
				server?.Stop(); // Stop the server if it was started
			}
		}
		private async void btnConnect_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				// Parse the IP:Port from the textbox
				string[] addressParts = txtFriendAddress.Text.Split(':');
				if (addressParts.Length != 2 || !int.TryParse(addressParts[1], out int port))
				{
					throw new Exception("Invalid address format. Please use IP:Port");
				}
				string ip = addressParts[0];

				// Create a TCP client and connect to the server
				TcpClient client = new TcpClient();
				txtStatus.Text = "Status: Connecting...";
				await client.ConnectAsync(ip, port);

				txtStatus.Text = "Status: Connected";
				MessageBox.Show("Connected to friend!");


				client.Close();
			}
			catch (Exception ex)
			{
				txtStatus.Text = "Status: Error";
				MessageBox.Show($"Error connecting to friend: {ex.Message}");
			}
		}
	}
}