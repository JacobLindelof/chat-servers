use base64::{engine::general_purpose, Engine as _};
use lazy_static::lazy_static;
use regex::Regex;
use serde::{Deserialize, Serialize};
use serde_json;
use sha1::{Digest, Sha1};
use std::{
    fmt::{self, Formatter},
    io::{Read, ErrorKind},
    io::Write,
    net::{TcpListener, TcpStream},
    sync::Mutex,
    thread,
};
use uuid::Uuid;

struct ChatClient {
    uuid: Uuid,
    stream: TcpStream,
    username: String,
}

impl ChatClient {
    fn new(uuid: Uuid, stream: TcpStream, username: String) -> ChatClient {
        ChatClient {
            uuid,
            stream,
            username,
        }
    }
}

#[derive(Serialize, Deserialize, Debug)]
struct WebSocketMessage {
    action: String,
    data: String,
}

impl WebSocketMessage {
    fn new(action: String, data: String) -> WebSocketMessage {
        WebSocketMessage { action, data }
    }
}

impl fmt::Display for WebSocketMessage {
    fn fmt(&self, f: &mut Formatter) -> fmt::Result {
        write!(
            f,
            "WebSocketMessage {{ action: {}, data: {} }}",
            self.action, self.data
        )
    }
}

fn preform_handshake(mut stream: &TcpStream, s: &str) {
    let re = Regex::new(r#"Sec-WebSocket-Key: (.*)"#).unwrap();
    let swk = re
        .captures(&s)
        .unwrap()
        .get(1)
        .unwrap()
        .as_str()
        .trim()
        .to_owned();
    let swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    let mut hasher = Sha1::new();
    hasher.update(swka);
    let hash = hasher.finalize();
    let swka_sha1_base64 = general_purpose::STANDARD_NO_PAD.encode(hash);

    let response = format!(
        "HTTP/1.1 101 Switching Protocols\r\n\
    Connection: Upgrade\r\n\
    Upgrade: websocket\r\n\
    Sec-WebSocket-Accept: {}=\r\n\r\n",
        swka_sha1_base64
    );

    stream.write(response.as_bytes()).unwrap();
}

fn decode_websocket_message(data: [u8; 1024]) -> WebSocketMessage {
    let mut decoded_data = String::new();
    let mut payload_length = data[1] as usize & 127;
    let mut mask_index = 2;
    if payload_length == 126 {
        mask_index = 4;
        payload_length = ((data[2] as usize) << 8) | data[3] as usize;
    } else if payload_length == 127 {
        mask_index = 10;
        payload_length = ((data[2] as usize) << 56)
            | ((data[3] as usize) << 48)
            | ((data[4] as usize) << 40)
            | ((data[5] as usize) << 32)
            | ((data[6] as usize) << 24)
            | ((data[7] as usize) << 16)
            | ((data[8] as usize) << 8)
            | data[9] as usize;
    }
    let mut masks = [0; 4];
    masks[0] = data[mask_index];
    masks[1] = data[mask_index + 1];
    masks[2] = data[mask_index + 2];
    masks[3] = data[mask_index + 3];
    let mut index = mask_index + 4;
    while index < mask_index + 4 + payload_length {
        decoded_data.push((data[index] ^ masks[(index - mask_index - 4) % 4]) as char);
        index += 1;
    }
    let message: WebSocketMessage = serde_json::from_str(&decoded_data).unwrap();
    return message;
}

fn generate_websocket_response(message: WebSocketMessage) -> Vec<u8> {
    let s = format!(
        "{{\"action\": \"{}\", \"data\": \"{}\"}}",
        message.action, message.data
    );
    let mut response = Vec::new();
    response.push(129);
    if s.len() <= 125 {
        response.push(s.len() as u8);
    } else if s.len() >= 126 && s.len() <= 65535 {
        response.push(126);
        response.push(((s.len() >> 8) & 255) as u8);
        response.push((s.len() & 255) as u8);
    } else {
        response.push(127);
        response.push(((s.len() >> 56) & 255) as u8);
        response.push(((s.len() >> 48) & 255) as u8);
        response.push(((s.len() >> 40) & 255) as u8);
        response.push(((s.len() >> 32) & 255) as u8);
        response.push(((s.len() >> 24) & 255) as u8);
        response.push(((s.len() >> 16) & 255) as u8);
        response.push(((s.len() >> 8) & 255) as u8);
        response.push((s.len() & 255) as u8);
    }
    for b in s.as_bytes() {
        response.push(*b);
    }
    return response;
}

fn broadcast_message(message: WebSocketMessage, sender_uuid: Uuid) {
    let b = generate_websocket_response(message);
    let mut clients = CLIENTS.lock().unwrap();
    for client in clients.iter_mut() {
        if client.uuid == sender_uuid {
            continue;
        }
        client.stream.write(b.as_slice()).unwrap();
    }
}

fn handle_client(mut stream: TcpStream) {
    thread::spawn(move || {
        let uuid = Uuid::new_v4();
        let mut username = "Anonymous".to_string();
        let chat_client = ChatClient::new(uuid, stream.try_clone().unwrap(), username.clone());  

        let mut clients = CLIENTS.lock().unwrap();
        clients.push(chat_client);
        drop(clients);

        broadcast_message(
            WebSocketMessage::new(
                "join".to_string(),
                format!("{} has joined the chat", username),
            ),
            uuid,
        );
    
        loop {
            let mut buf = [0; 1024];
            match stream.read(&mut buf) {
                Ok(n) => {
                    if n == 0 {
                        break;
                    }

                    let s = String::from_utf8_lossy(&buf[..]);
                    if s.starts_with("GET") {
                        preform_handshake(&stream, &s);
                    } else {
                        let opcode = buf[0] & 15;
                        if opcode == 8 {
                            let mut clients = CLIENTS.lock().unwrap();
                            clients.retain(|c| c.uuid != uuid);
                            drop(clients);
                            break;
                        }
                        let message = decode_websocket_message(buf);
                        if message.action == "message" {
                            let new_message = WebSocketMessage::new(
                                "message".to_string(),
                                format!("{}: {}", username, message.data),
                            );
                            broadcast_message(new_message, uuid)
                        } else if message.action == "username" {
                            let username_message = WebSocketMessage::new(
                                "username".to_string(),
                                format!("{} changed their username to {}", username, message.data),
                            );
                            broadcast_message(username_message, uuid);
                            username = message.data.clone();

                            let mut clients = CLIENTS.lock().unwrap();
                            for client in clients.iter_mut() {
                                if client.uuid == uuid {
                                    client.username = message.data.clone();                                    
                                }
                            }

                        }
                    }
                }
                Err(ref e) if e.kind() == ErrorKind::WouldBlock => {
                    // println!("Nothing to read");
                }
                Err(_) => {
                    println!("An error occurred, terminating connection with {}", stream.peer_addr().unwrap());
                }
            }
        }
    });
}

lazy_static! {
    static ref CLIENTS: Mutex<Vec<ChatClient>> = Mutex::new(Vec::new());
}

fn main() -> std::io::Result<()> {
    let server = TcpListener::bind("127.0.0.1:8080")?;

    server
        .set_nonblocking(true)
        .expect("Cannot set non-blocking");
    println!("Server listening on port 8080");

    // accept connections and process them serially
    loop {
        if let Ok((socket, _addr)) = server.accept() {
            handle_client(socket);
        }
    }
}
