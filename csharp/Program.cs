using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public class WebSocketMessage
{
  public bool FIN { get; set; } = false;
  public bool Mask { get; set; } = false;
  public int OPCODE { get; set; } = 0;
  public string Message { get; set; } = String.Empty;

  public WebSocketMessage(byte[] bytes)
  {
    this.FIN = (bytes[0] & 0b10000000) != 0;
    this.Mask = (bytes[1] & 0b10000000) != 0;
    this.OPCODE = bytes[0] & 0b00001111;

    int offset = 2;
    ulong messageLength = (ulong)(bytes[1] & 0b01111111);

    byte[] decoded = new byte[messageLength];
    byte[] masks = new byte[4] { bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3] };
    offset += 4;

    for (ulong i = 0; i < messageLength; ++i)
      decoded[i] = (byte)(bytes[(ulong)offset + i] ^ masks[i % 4]);

    this.Message = Encoding.UTF8.GetString(decoded);
  }
}

class WebSocketAction
{
  public string action { get; set; } = String.Empty;
  public string data { get; set; } = String.Empty;
}

class ChatClient
{
  public Guid uuid { get; set; }
  public TcpClient client { get; set; }
  public string username { get; set; }

  public ChatClient(Guid uuid, TcpClient client, string username)
  {
    this.uuid = uuid;
    this.client = client;
    this.username = username;
  }
}

class Server
{
  static List<ChatClient> clients = new List<ChatClient>();

  public static void Main()
  {
    string ip = "127.0.0.1";
    int port = 8080;
    var server = new TcpListener(IPAddress.Parse(ip), port);

    server.Start();
    Console.WriteLine("Server has started on {0}:{1}, Waiting for a connection…", ip, port);

    while (true)
    {
      TcpClient client = server.AcceptTcpClient();
      Thread t = new Thread(new ParameterizedThreadStart(HandleConnection));
      t.Start(client);
    }
  }

  static void DoHandshake(string s, NetworkStream stream)
  {
    string swk = Regex.Match(s, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
    string swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    byte[] swkaSha1 = System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swka));
    string swkaSha1Base64 = Convert.ToBase64String(swkaSha1);

    byte[] response = Encoding.UTF8.GetBytes(
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Connection: Upgrade\r\n" +
        "Upgrade: websocket\r\n" +
        "Sec-WebSocket-Accept: " + swkaSha1Base64 + "\r\n\r\n");

    stream.Write(response, 0, response.Length);
  }

  static void BroadcastMessage(string action, string message, TcpClient? originatingClient)
  {
    string s = "{\"action\":\"" + action + "\",\"data\":\"" + message + "\"}";
    byte[] bytes = Encoding.UTF8.GetBytes(s);
    byte[] response = new byte[bytes.Length + 2];
    response[0] = 0b10000001;
    response[1] = (byte)bytes.Length;
    bytes.CopyTo(response, 2);
    clients.ForEach(c =>
    {
      if (c.client != originatingClient)
        c.client.GetStream().Write(response, 0, response.Length);
    });
  }

  static void SendMessage(string action, string message, TcpClient client)
  {
    string s = "{\"action\":\"" + action + "\",\"data\":\"" + message + "\"}";
    byte[] bytes = Encoding.UTF8.GetBytes(s);
    byte[] response = new byte[bytes.Length + 2];
    response[0] = 0b10000001;
    response[1] = (byte)bytes.Length;
    bytes.CopyTo(response, 2);
    client.GetStream().Write(response, 0, response.Length);
  }

  static void HandleConnection(object? obj)
  {
    if (obj == null) return;

    var client = (TcpClient)obj;

    if (!client.Connected) return;

    ChatClient chatClient = new ChatClient(Guid.NewGuid(), client, "Anonymous");

    NetworkStream stream = client.GetStream();

    while (true)
    {
      while (!stream.DataAvailable) ;
      while (client.Available < 3) ;

      byte[] bytes = new byte[client.Available];
      stream.Read(bytes, 0, client.Available);
      string s = Encoding.UTF8.GetString(bytes);

      if (Regex.IsMatch(s, "^GET", RegexOptions.IgnoreCase))
      {
        DoHandshake(s, stream);
        BroadcastMessage("join", chatClient.username + " has joined the chat", client);
        clients.Add(chatClient);
        Console.WriteLine("Client {0} Connected", chatClient.uuid);
      }
      else
      {
        try
        {
          WebSocketMessage message = new WebSocketMessage(bytes);

          if (message.OPCODE == 8)
          {
            Console.WriteLine("Client {0} Disconnected", chatClient.uuid);
            BroadcastMessage("leave", chatClient.username + " has left the chat", client);
            clients.Remove(chatClient);
            client.Close();
            return;
          }

          WebSocketAction? action = JsonSerializer.Deserialize<WebSocketAction>(message.Message);
          
          if (action == null || action.action == null || action.data == null)
          {
            SendMessage("error", "Invalid action", client);
            return;
          }

          switch(action.action) 
          {
            case "message":
              BroadcastMessage("message", chatClient.username + ": " + action.data, client);
              break;
            case "username":
              BroadcastMessage("username", chatClient.username + " changed their name to " + action.data, client);
              chatClient.username = action.data;
              break;
            default:
              SendMessage("error", "Invalid action", client);
              break;
          }
        }
        catch (Exception e)
        {
          SendMessage("error", e.Message, client);
        }
      }
    }
  }
}