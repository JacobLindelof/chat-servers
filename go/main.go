package main

import (
	"crypto/sha1"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net"
	"regexp"
	"strings"

	"github.com/google/uuid"
)

type ChatClient struct {
	uuid     string
	conn     net.Conn
	username string
}

type WebSocketMessage struct {
	Action string `json:"action"`
	Data   string `json:"data"`
}

var chatClients = []ChatClient{}

func preformHandshake(conn net.Conn, s string) {
	headerRegex := regexp.MustCompile("Sec-WebSocket-Key: (.*)")
	hasher := sha1.New()
	swk := strings.Split(headerRegex.FindString(s), ": ")[1]
	swk = strings.TrimSpace(swk)
	swka := swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
	hasher.Write([]byte(swka))
	swkaSha1Base64 := base64.StdEncoding.EncodeToString(hasher.Sum(nil))

	response := "HTTP/1.1 101 Switching Protocols\r\n" +
		"Connection: Upgrade\r\n" +
		"Upgrade: websocket\r\n" +
		"Sec-WebSocket-Accept: " +
		swkaSha1Base64 +
		"\r\n\r\n"

	conn.Write([]byte(response))
}

func generateWebSocketMessage(action string, data string) []byte {
	message := "{\"action\": \"" + action + "\", \"data\": \"" + data + "\"}"
	messageBytes := []byte(message)
	bytes := make([]byte, len(messageBytes)+2)
	bytes[0] = 129
	bytes[1] = byte(len(messageBytes))
	for i, b := range messageBytes {
		bytes[i+2] = b
	}
	return bytes
}

func sendWebSocketMessage(conn net.Conn, action string, data string) {
	conn.Write(generateWebSocketMessage(action, data))
}

func broadcastWebSocketMessage(action string, data string, originatingClient ChatClient) {
	message := generateWebSocketMessage(action, data)
	for _, chatClient := range chatClients {
		if chatClient.uuid != originatingClient.uuid {
			chatClient.conn.Write(message)
		}
	}
}

func decodeWebSocketMessage(data []byte) WebSocketMessage {
	length := data[1] & 0b01111111
	mask := data[2:6]
	decoded := data[6 : 6+length]

	for i, b := range decoded {
		decoded[i] = b ^ mask[i%4]
	}

	var message WebSocketMessage

	err := json.Unmarshal(decoded, &message)
	if err != nil {
		fmt.Println(err)
	}

	return message
}

func handleConnection(conn net.Conn) {
	defer conn.Close()

	fmt.Printf("New connection from %s\n", conn.RemoteAddr().String())
	chatClient := ChatClient{uuid.New().String(), conn, "Anonymous"}

	getRegex := regexp.MustCompile("^GET")

	for {
		data := make([]byte, 1024)
		n, err := conn.Read(data)
		fmt.Printf("Chat client: %s\n", chatClient.uuid)

		if err != nil {
			fmt.Printf("Client disconnected: %s\n", err.Error())
			return
		}

		if n > 0 {
			s := string(data[:n])
			match := getRegex.MatchString(s)
			if match {
				preformHandshake(conn, s)
				chatClients = append(chatClients, chatClient)
				broadcastWebSocketMessage("message", chatClient.username+" has joined the chat", chatClient)
			} else {
				message := decodeWebSocketMessage(data)
				if message.Action == "username" {
					fmt.Printf("Username changed to: %s\n", message.Data)
					oldUsername := chatClient.username
					chatClient.username = message.Data
					broadcastWebSocketMessage("username", oldUsername+" has changed their username to "+chatClient.username, chatClient)
				} else if message.Action == "message" {
					broadcastWebSocketMessage("message", chatClient.username+": "+message.Data, chatClient)
				}
			}
		}
	}
}

func main() {
	addr := "127.0.0.1:8080"
	listener, err := net.Listen("tcp", addr)

	if err != nil {
		panic(err)
	}

	defer listener.Close()

	host, port, err := net.SplitHostPort(listener.Addr().String())
	if err != nil {
		panic(err)
	}

	fmt.Printf("Listening on host: %s, port: %s\n", host, port)

	for {
		conn, err := listener.Accept()
		if err != nil {
			panic(err)
		}

		go handleConnection(conn)
	}

}
