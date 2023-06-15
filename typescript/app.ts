import net from "net";
import crypto, { UUID } from "crypto";

type ChatClient = {
  uuid: UUID;
  socket: net.Socket;
  username: string;
};

type WebSocketMessage = {
  action: string;
  data: string;
}

let clients: ChatClient[] = [];

const preformHandshake = (data: Buffer, socket: net.Socket) => {
  const s = data.toString();
  const swk = s.match("Sec-WebSocket-Key: (.*)")[0].trim().split(": ")[1];
  const swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
  const swkaSha1Base64 = crypto
    .createHash("sha1")
    .update(swka)
    .digest("base64");
  socket.write(
    "HTTP/1.1 101 Switching Protocols\r\n" +
      "Connection: Upgrade\r\n" +
      "Upgrade: websocket\r\n" +
      "Sec-WebSocket-Accept: " +
      swkaSha1Base64 +
      "\r\n\r\n"
  );
};

const generateWebSocketMessage = (action: string, data: string) => {
  const message = JSON.stringify({ action, data });
  const dataBuffer = Buffer.from(message);
  const response = Buffer.alloc(dataBuffer.length + 2);
  response[0] = 0b10000001;
  response[1] = dataBuffer.length;
  dataBuffer.copy(response, 2);
  return response;
}

const sendWebSocketMessage = (socket: net.Socket, action: string, data: string) => {
  const message = generateWebSocketMessage(action, data);
  socket.write(message);
}

const broadcastWebSocketMessage = (action: string, data: string, originatingClient: ChatClient) => {
  const message = generateWebSocketMessage(action, data);
  clients.filter(c => {
    return c.uuid !== originatingClient.uuid;
  }).forEach((s) => {
    s.socket.write(message);
  });
}

const decodeWebSocketMessage = (data: Buffer): WebSocketMessage => {
  const length = data[1] & 0b01111111;
  const maskStart = 2;
  const dataStart = maskStart + 4;
  const mask = data.subarray(maskStart, dataStart);
  const encoded = data.subarray(dataStart, dataStart + length);
  const decoded = Buffer.alloc(encoded.length);
  for (let i = 0; i < encoded.length; i++) {
    decoded[i] = encoded[i] ^ mask[i % 4];
  }
  const message = JSON.parse(decoded.toString());
  if (!message.action || !message.data) {
    throw new Error("Invalid message.");
  }
  return message;
}

const server = net.createServer((socket) => {

  const chatClient: ChatClient = {
    uuid: crypto.randomUUID(),
    socket: socket,
    username: "Anonymous"
  };
  clients.push(chatClient);

  console.log("Client connected.");

  socket.on("data", (data) => {
    if (data.toString().match(/^GET/)) {
      preformHandshake(data, socket);
      broadcastWebSocketMessage("message", `${chatClient.username} has joined the chat.`, chatClient);
    } else {
      const opcode = data[0] & 0b00001111;
      if (opcode === 8) {
        socket.end();
        clients = clients.filter((s) => s.uuid !== chatClient.uuid);
      }
      else {
        try {
          const message = decodeWebSocketMessage(data);
          if (message.action === "message") {
            broadcastWebSocketMessage("message", `${chatClient.username}: ${message.data}`, chatClient);
          } else if (message.action === "username") {
            const oldUsername = chatClient.username;
            chatClient.username = message.data;
            broadcastWebSocketMessage("message", `${oldUsername} has changed their username to ${chatClient.username}.`, chatClient);
          }
        }
        catch (err) {
          sendWebSocketMessage(socket, "error", err.message);
        }
      }
    }
  });

  socket.on("error", (err) => {
    console.log("A client has disconnected.");
  });

  socket.on("close", () => {
    console.log("A client has left the chat.");
  });
});

server.listen(8080, () => {
  console.log("Server started. Listening on port 8080.");
});
