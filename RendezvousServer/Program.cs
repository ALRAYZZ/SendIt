using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace RendezvousServer
{
    class Program
    {
        static async Task Main()
        {
            UdpClient server = new UdpClient(5001);
			Console.WriteLine("Rendezvous server running on port 5001...");

            IPEndPoint peer1 = null;
            IPEndPoint peer2 = null;

            while (true)
            {
                UdpReceiveResult result = await server.ReceiveAsync();
                string message = Encoding.UTF8.GetString(result.Buffer);
                Console.WriteLine($"Received: {message} from {result.RemoteEndPoint}");

                if (message == "PEER1")
                {
                    peer1 = result.RemoteEndPoint;
                    Console.WriteLine($"Peer1 registered: {peer1}");
                }
                else if (message == "PEER2")
                {
                    peer2 = result.RemoteEndPoint;
                    Console.WriteLine($"Peer2 registered: {peer2}");
                }

				if (peer1 != null && peer2 != null)
				{
					string peer2Endpoint = $"{peer2.Address}:{peer2.Port}";
                    byte[] peer2Bytes = Encoding.UTF8.GetBytes(peer2Endpoint);
                    await server.SendAsync(peer2Bytes, peer2Bytes.Length, peer1);

					string peer1Endpoint = $"{peer1.Address}:{peer1.Port}";
					byte[] peer1Bytes = Encoding.UTF8.GetBytes(peer1Endpoint);
					await server.SendAsync(peer1Bytes, peer1Bytes.Length, peer2);

					Console.WriteLine("Endpoints send. Resetting...");
					peer1 = null;
					peer2 = null;
				}
			}
		}
    }
}
