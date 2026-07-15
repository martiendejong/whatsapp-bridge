import express from 'express';
import pkg from 'whatsapp-web.js';
const { Client, LocalAuth } = pkg;
import qrcode from 'qrcode';
import cors from 'cors';
import { writeFileSync, existsSync, readFileSync } from 'fs';

const app = express();
const PORT = process.env.PORT || 3000;
const CHROME_PATH = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const QR_DIR = 'C:\\temp';

app.use(cors());
app.use(express.json());

const clients = new Map();
const readySessions = new Set(); // sessions that have fired 'ready'

function withTimeout(promise, ms = 20000) {
    return Promise.race([
        promise,
        new Promise((_, reject) => setTimeout(() => reject(new Error('Timed out after ' + ms + 'ms')), ms))
    ]);
}

app.get('/health', (req, res) => {
    res.json({ status: 'ok', activeSessions: clients.size, readySessions: readySessions.size });
});

app.post('/session/create', async (req, res) => {
    try {
        const { sessionId } = req.body;
        if (!sessionId) return res.status(400).json({ error: 'sessionId is required' });
        if (clients.has(sessionId)) return res.status(400).json({ error: 'Session already exists' });

        const client = new Client({
            authStrategy: new LocalAuth({ clientId: sessionId }),
            puppeteer: {
                headless: true,
                executablePath: CHROME_PATH,
                args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-dev-shm-usage']
            }
        });

        let qrCodeData = null;

        client.on('qr', async (qr) => {
            console.log('QR code received for session ' + sessionId);
            qrCodeData = await qrcode.toDataURL(qr);
            try {
                writeFileSync(QR_DIR + '\\wa_qr_' + sessionId + '.txt', qrCodeData, 'utf8');
            } catch (e) {
                console.error('Failed to write QR file:', e.message);
            }
        });

        client.on('ready', () => {
            console.log('WhatsApp client ready for session ' + sessionId);
            readySessions.add(sessionId);
        });
        client.on('authenticated', () => console.log('Session ' + sessionId + ' authenticated'));
        client.on('auth_failure', (msg) => console.error('Auth failure for session ' + sessionId + ':', msg));
        client.on('disconnected', (reason) => {
            console.log('Session ' + sessionId + ' disconnected:', reason);
            clients.delete(sessionId);
            readySessions.delete(sessionId);
        });

        clients.set(sessionId, client);
        await client.initialize();

        let attempts = 0;
        while (!qrCodeData && attempts < 60) {
            await new Promise(resolve => setTimeout(resolve, 500));
            attempts++;
        }

        res.json({ sessionId, qrCode: qrCodeData });
    } catch (error) {
        console.error('Error creating session:', error);
        res.status(500).json({ error: error.message });
    }
});


app.get('/diag', (req, res) => {
    const { sessionId } = req.query;
    const c = clients.get(sessionId);
    res.json({
        hasClient: !!c,
        hasPupPage: !!(c && c.pupPage),
        pupPageType: c && c.pupPage ? typeof c.pupPage : 'none',
        isReady: readySessions.has(sessionId),
        activeSessions: clients.size
    });
});

app.get('/session/:sessionId/qr', (req, res) => {
    const { sessionId } = req.params;
    const filePath = QR_DIR + '\\wa_qr_' + sessionId + '.txt';
    if (!existsSync(filePath)) return res.status(404).json({ error: 'No QR available yet' });
    const qrCode = readFileSync(filePath, 'utf8').trim();
    res.json({ sessionId, qrCode });
});

app.get('/qr-page/:sessionId', (req, res) => {
    const { sessionId } = req.params;
    res.setHeader('Content-Type', 'text/html');
    res.send(`<!DOCTYPE html>
<html>
<head><title>WhatsApp QR</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
  body{background:#111;display:flex;flex-direction:column;align-items:center;justify-content:center;min-height:100vh;margin:0;font-family:sans-serif;color:#fff;}
  img{width:320px;height:320px;border:10px solid white;border-radius:8px;display:block;}
  p{margin-top:16px;font-size:15px;color:#aaa;text-align:center;}
  #status{font-size:12px;color:#666;margin-top:8px;}
</style>
</head>
<body>
<h2 style="margin-bottom:16px">Scan with WhatsApp</h2>
<img id="qr" src="" alt="QR loading..." />
<p>WhatsApp &rarr; Linked Devices &rarr; Link a Device</p>
<p id="status">Loading...</p>
<script>
async function refresh(){
  try{
    const r=await fetch('/session/${sessionId}/qr');
    if(!r.ok){document.getElementById('status').textContent='Waiting for QR...';return;}
    const d=await r.json();
    if(d.qrCode){document.getElementById('qr').src=d.qrCode;document.getElementById('status').textContent='Refreshed '+new Date().toLocaleTimeString();}
  }catch(e){document.getElementById('status').textContent='Error: '+e.message;}
}
refresh();setInterval(refresh,15000);
</script>
</body></html>`);
});

app.get('/session/:sessionId/status', async (req, res) => {
    const { sessionId } = req.params;
    const client = clients.get(sessionId);
    if (!client) return res.status(404).json({ error: 'Session not found' });
    try {
        const state = await withTimeout(client.getState(), 10000);
        res.json({ sessionId, state, ready: readySessions.has(sessionId) });
    } catch (error) {
        res.json({ sessionId, state: 'UNKNOWN', ready: readySessions.has(sessionId), error: error.message });
    }
});

app.delete('/session/:sessionId', async (req, res) => {
    const { sessionId } = req.params;
    const client = clients.get(sessionId);
    if (!client) return res.status(404).json({ error: 'Session not found' });
    try {
        await client.destroy();
        clients.delete(sessionId);
        res.json({ message: 'Session disconnected' });
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

app.post('/message/send', async (req, res) => {
    const { sessionId, to, body } = req.body;
    if (!sessionId || !to || !body) return res.status(400).json({ error: 'sessionId, to, and body are required' });
    const client = clients.get(sessionId);
    if (!client) return res.status(404).json({ error: 'Session not found' });
    try {
        const chatId = to.includes('@c.us') ? to : to + '@c.us';
        const message = await client.sendMessage(chatId, body);
        res.json({ id: message.id._serialized, from: message.from, to: message.to, body: message.body, timestamp: message.timestamp });
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

app.post('/message/sendMedia', async (req, res) => {
    const { sessionId, to, mediaUrl, caption } = req.body;
    if (!sessionId || !to || !mediaUrl) return res.status(400).json({ error: 'sessionId, to, and mediaUrl are required' });
    const client = clients.get(sessionId);
    if (!client) return res.status(404).json({ error: 'Session not found' });
    try {
        const chatId = to.includes('@c.us') ? to : to + '@c.us';
        const { MessageMedia } = await import('whatsapp-web.js');
        const media = await MessageMedia.fromUrl(mediaUrl);
        const message = await client.sendMessage(chatId, media, { caption });
        res.json({ id: message.id._serialized, from: message.from, to: message.to, timestamp: message.timestamp });
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

app.get('/messages', async (req, res) => {
    const { sessionId, chatId, limit = 50 } = req.query;
    if (!sessionId || !chatId) return res.status(400).json({ error: 'sessionId and chatId are required' });
    const client = clients.get(sessionId);
    if (!client) return res.status(404).json({ error: 'Session not found' });
    try {
        let chat;
        console.log('MSG: getChatById start ' + chatId);
        try {
            chat = await withTimeout(client.getChatById(chatId), 20000);
            console.log('MSG: getChatById ok');
        } catch (e) {
            console.log('MSG: getChatById failed: ' + e.message + ' - trying getChats fallback');
            const chats = await withTimeout(client.getChats(), 90000);
            console.log('MSG: getChats returned ' + chats.length + ' chats');
            chat = chats.find(c => c.id._serialized === chatId);
            if (!chat) return res.status(404).json({ error: 'Chat not found: ' + chatId + '. getChatById: ' + e.message });
        }
        console.log('MSG: fetchMessages start');
        const messages = await withTimeout(chat.fetchMessages({ limit: parseInt(limit) }), 30000);
        console.log('MSG: fetchMessages returned ' + messages.length);
        res.json(messages.map(msg => ({
            id: msg.id._serialized, from: msg.from, to: msg.to,
            body: msg.body, hasMedia: msg.hasMedia, type: msg.type, timestamp: msg.timestamp
        })));
    } catch (error) {
        console.error('MSG error: ' + error.message);
        res.status(500).json({ error: error.message });
    }
});

// Direct Puppeteer store query — bypasses whatsapp-web.js abstraction
app.get('/messages/raw', async (req, res) => {
    const { sessionId, chatId, limit = 50 } = req.query;
    const client = clients.get(sessionId);
    if (!client) return res.status(404).json({ error: 'Session not found' });
    try {
        console.log('RAW: evaluating for ' + chatId);
        const result = await withTimeout(client.pupPage.evaluate(async (chatId, limit) => {
            try {
                const store = window.Store;
                if (!store || !store.Chat) return { error: 'Store not available' };

                const num = chatId.replace('@c.us', '').replace('@lid', '');
                let chat = store.Chat.get(chatId)
                    || store.Chat.getModelsArray().find(c =>
                        c.id._serialized === chatId || c.id.user === num);

                if (!chat) {
                    const available = store.Chat.getModelsArray()
                        .slice(0, 20)
                        .map(c => (c.id ? c.id._serialized : '?') + ' ' + (c.name || ''));
                    return { error: 'Chat not found. First 20: ' + available.join(' | ') };
                }

                const msgs = chat.msgs.getModelsArray().slice(-limit);
                return msgs.map(m => ({
                    id: m.id ? m.id._serialized : null,
                    type: m.type || '',
                    body: m.body || '',
                    hasMedia: !!m.hasMedia,
                    timestamp: m.t || 0,
                    fromMe: m.id ? !!m.id.fromMe : false,
                    mediaKey: m.mediaKey || null
                }));
            } catch (e) {
                return { error: 'evaluate threw: ' + String(e) };
            }
        }, chatId, parseInt(limit)), 30000);
        console.log('RAW: evaluate done, result type=' + typeof result);
        if (result && result.error) return res.status(500).json(result);
        res.json(result);
    } catch (error) {
        console.error('RAW error: ' + error.message);
        res.status(500).json({ error: String(error) });
    }
});

app.get('/messages/media', async (req, res) => {
    const { sessionId, messageId } = req.query;
    if (!sessionId || !messageId) return res.status(400).json({ error: 'sessionId and messageId are required' });
    const client = clients.get(sessionId);
    if (!client) return res.status(404).json({ error: 'Session not found' });
    try {
        const msg = await client.getMessageById(messageId);
        if (!msg) return res.status(404).json({ error: 'Message not found' });
        if (!msg.hasMedia) return res.status(400).json({ error: 'Message has no media' });
        const media = await msg.downloadMedia();
        res.json({ mimetype: media.mimetype, data: media.data, filename: media.filename || null });
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

app.get('/chats', async (req, res) => {
    const { sessionId } = req.query;
    if (!sessionId) return res.status(400).json({ error: 'sessionId is required' });
    const client = clients.get(sessionId);
    if (!client) return res.status(404).json({ error: 'Session not found' });
    try {
        const chats = await withTimeout(client.getChats(), 120000);
        res.json(chats.map(chat => ({
            id: chat.id._serialized, name: chat.name,
            isGroup: chat.isGroup, unreadCount: chat.unreadCount, timestamp: chat.timestamp
        })));
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

app.get('/contacts', async (req, res) => {
    const { sessionId } = req.query;
    if (!sessionId) return res.status(400).json({ error: 'sessionId is required' });
    const client = clients.get(sessionId);
    if (!client) return res.status(404).json({ error: 'Session not found' });
    try {
        const contacts = await client.getContacts();
        res.json(contacts.map(c => ({
            id: c.id._serialized, name: c.name || c.pushname, number: c.number, isMyContact: c.isMyContact
        })));
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

app.get('/number/check', async (req, res) => {
    const { sessionId, number } = req.query;
    if (!sessionId || !number) return res.status(400).json({ error: 'sessionId and number are required' });
    const client = clients.get(sessionId);
    if (!client) return res.status(404).json({ error: 'Session not found' });
    try {
        const numberId = number.includes('@c.us') ? number : number + '@c.us';
        const isRegistered = await client.isRegisteredUser(numberId);
        res.json({ number, isRegistered });
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

app.listen(PORT, '0.0.0.0', () => {
    console.log('WhatsApp Bridge Service running on 0.0.0.0:' + PORT);
    console.log('Accessible via http://localhost:' + PORT + ' and http://85.215.217.154:' + PORT);
});
