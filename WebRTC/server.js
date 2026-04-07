// server.js
const WebSocket = require('ws');
const wss = new WebSocket.Server({ port: 3001 });
const rooms = new Map();
function getRoom(id){ if(!rooms.has(id)) rooms.set(id, new Set()); return rooms.get(id); }

wss.on('connection', (ws, req) => {
  const url = new URL(req.url, 'ws://localhost');
  const roomId = url.searchParams.get('room') || 'default';
  const peers = getRoom(roomId);
  peers.add(ws);
  ws.on('message', msg => { for (const p of peers) if (p!==ws && p.readyState===1) p.send(msg); });
  ws.on('close', () => { peers.delete(ws); if (!peers.size) rooms.delete(roomId); });
});
console.log('Signaling on ws://127.0.0.1:3001?room=demo');