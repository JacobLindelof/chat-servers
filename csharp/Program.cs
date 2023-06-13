using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public class WebSocketMessage
{
  public string? action { get; set; }
  public string? data { get; set; }
}
class Server
{
  static List<TcpClient> clients = new List<TcpClient>();

  public static void Main()
  {
    string ip = "127.0.0.1";
    int port = 8080;
    var server = new TcpListener(IPAddress.Parse(ip), port);

    server.Start();
    Console.WriteLine("Server has started on {0}:{1}, Waiting for a connection…", ip, port);

    server.BeginAcceptTcpClient(HandleConnection, server);

    Console.ReadLine();
  }

  static byte[] GetHandshake(string s)
  {
    string swk = Regex.Match(s, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
    string swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    byte[] swkaSha1 = System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swka));
    string swkaSha1Base64 = Convert.ToBase64String(swkaSha1);

    return Encoding.UTF8.GetBytes(
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Connection: Upgrade\r\n" +
        "Upgrade: websocket\r\n" +
        "Sec-WebSocket-Accept: " + swkaSha1Base64 + "\r\n\r\n");
  }

  static string DecodeMessage(byte[] bytes, int offset, ulong length, bool mask)
  {
    byte[] decoded = new byte[length];
    byte[] masks = new byte[4] { bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3] };
    offset += 4;

    for (ulong i = 0; i < length; ++i)
      decoded[i] = (byte)(bytes[(ulong)offset + i] ^ masks[i % 4]);

    return Encoding.UTF8.GetString(decoded);
  }

  static void HandleConnection(IAsyncResult ar)
  {
    if (ar.AsyncState is null)
      throw new Exception("AsyncState is null. Pass it as an argument to BeginAcceptSocket method");

    TcpListener server = (TcpListener)ar.AsyncState;

    server.BeginAcceptTcpClient(HandleConnection, server);

    using TcpClient client = server.EndAcceptTcpClient(ar);

    NetworkStream stream = client.GetStream();

    while (true)
    {
      while (!stream.DataAvailable) ;
      while (client.Available < 3) ; // match against "get"

      byte[] bytes = new byte[client.Available];
      stream.Read(bytes, 0, client.Available);
      string s = Encoding.UTF8.GetString(bytes);

      if (Regex.IsMatch(s, "^GET", RegexOptions.IgnoreCase))
      {
        byte[] handshake = GetHandshake(s);
        stream.Write(handshake, 0, handshake.Length);
        clients.Add(client);
      }
      else
      {
        try
        {
          bool fin = (bytes[0] & 0b10000000) != 0;
          bool mask = (bytes[1] & 0b10000000) != 0;
          int opcode = bytes[0] & 0b00001111;
          int offset = 2;
          ulong msglen = (ulong)(bytes[1] & 0b01111111);

          if (opcode == 8)
          {
            clients.Remove(client);
            client.Close();
            break;
          }

          string message = DecodeMessage(bytes, offset, msglen, mask);
          WebSocketMessage? msg = JsonSerializer.Deserialize<WebSocketMessage>(message);
          if (msg?.action == "message" && msg?.data != null)
          {
            clients.ForEach(c =>
            {
              if (c != client)
              {
                string message = "{\"action\":\"message\",\"data\":\"" + msg.data + "\"}";
                byte[] response = new byte[message.Length + 2];
                response[0] = 0b10000001;
                response[1] = (byte)message.Length;
                Encoding.UTF8.GetBytes(message).CopyTo(response, 2);
                c.GetStream().Write(response, 0, response.Length);
              }
            });
          }
        }
        catch (Exception e)
        {
          Console.WriteLine(e.Message);
        }
      }
    }
  }
}