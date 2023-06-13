# WebSocket Chat Clients
This repository is a collection of basic websocket chat clients intended to be used as practice for learning new languagues. 

## Requirments
Each repository should implement the following.
1. TCP Server that listens on port 8080.
2. Asynchronous/Threaded client connection process.
3. WebSocket Handshake implementation.
4. Close client connection on websocket close frame. (OPCODE 8)
5. Accept JSON messages from client.
6. Implement handlers for the `join`, `leave`, and `message` actions.
7. Keep track of connected clients.
7. Broadcast messages to all connected clients.
8. Broadcast `join` and `leave` messages to all connected clients.

## Data Structure
```json
{
    "action": "join|leave|message", // Action to perform
    "data": "string" // Username for join and leave actions, message for message action.
}