import express from 'express';
import pkg from 'whatsapp-web.js';
const { Client, LocalAuth } = pkg;
import qrcode from 'qrcode';
import cors from 'cors';

const app = express();
const PORT = process.env.PORT || 3000;

app.use(cors());
app.use(express.json());

// Store active WhatsApp clients
const clients = new Map();

// Health check
app.get('/health', (req, res) => {
    res.json({ status: 'ok', activeSessions: clients.size });
});

// Create new WhatsApp session and get QR code
app.post('/sessions/create', async (req, res) => {
    try {
        const { sessionId } = req.body;

        if (!sessionId) {
            return res.status(400).json({ error: 'sessionId is required' });
        }

        if (clients.has(sessionId)) {
            return res.status(400).json({ error: 'Session already exists' });
        }

        const client = new Client({
            authStrategy: new LocalAuth({ clientId: sessionId }),
            puppeteer: {
                headless: true,
                args: ['--no-sandbox', '--disable-setuid-sandbox']
            }
        });

        let qrCodeData = null;

        client.on('qr', async (qr) => {
            console.log(`QR code received for session ${sessionId}`);
            qrCodeData = await qrcode.toDataURL(qr);
        });

        client.on('ready', () => {
            console.log(`WhatsApp client ready for session ${sessionId}`);
        });

        client.on('authenticated', () => {
            console.log(`Session ${sessionId} authenticated`);
        });

        client.on('auth_failure', (msg) => {
            console.error(`Auth failure for session ${sessionId}:`, msg);
        });

        client.on('disconnected', (reason) => {
            console.log(`Session ${sessionId} disconnected:`, reason);
            clients.delete(sessionId);
        });

        clients.set(sessionId, client);
        await client.initialize();

        // Wait for QR code (max 30 seconds)
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

// Get session status
app.get('/sessions/:sessionId/status', async (req, res) => {
    const { sessionId } = req.params;
    const client = clients.get(sessionId);

    if (!client) {
        return res.status(404).json({ error: 'Session not found' });
    }

    try {
        const state = await client.getState();
        res.json({ sessionId, state });
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

// Disconnect and delete session
app.delete('/sessions/:sessionId', async (req, res) => {
    const { sessionId } = req.params;
    const client = clients.get(sessionId);

    if (!client) {
        return res.status(404).json({ error: 'Session not found' });
    }

    try {
        await client.destroy();
        clients.delete(sessionId);
        res.json({ message: 'Session disconnected' });
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

// Send text message
app.post('/messages/send', async (req, res) => {
    const { sessionId, to, body } = req.body;

    if (!sessionId || !to || !body) {
        return res.status(400).json({ error: 'sessionId, to, and body are required' });
    }

    const client = clients.get(sessionId);
    if (!client) {
        return res.status(404).json({ error: 'Session not found' });
    }

    try {
        const chatId = to.includes('@c.us') ? to : `${to}@c.us`;
        const message = await client.sendMessage(chatId, body);
        res.json({
            id: message.id._serialized,
            from: message.from,
            to: message.to,
            body: message.body,
            timestamp: message.timestamp
        });
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

// Send media (image, video, document)
app.post('/messages/sendMedia', async (req, res) => {
    const { sessionId, to, mediaUrl, caption } = req.body;

    if (!sessionId || !to || !mediaUrl) {
        return res.status(400).json({ error: 'sessionId, to, and mediaUrl are required' });
    }

    const client = clients.get(sessionId);
    if (!client) {
        return res.status(404).json({ error: 'Session not found' });
    }

    try {
        const chatId = to.includes('@c.us') ? to : `${to}@c.us`;
        const { MessageMedia } = await import('whatsapp-web.js');
        const media = await MessageMedia.fromUrl(mediaUrl);
        const message = await client.sendMessage(chatId, media, { caption });
        res.json({
            id: message.id._serialized,
            from: message.from,
            to: message.to,
            timestamp: message.timestamp
        });
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

// Get messages from a chat
app.get('/messages', async (req, res) => {
    const { sessionId, chatId, limit = 50 } = req.query;

    if (!sessionId || !chatId) {
        return res.status(400).json({ error: 'sessionId and chatId are required' });
    }

    const client = clients.get(sessionId);
    if (!client) {
        return res.status(404).json({ error: 'Session not found' });
    }

    try {
        const chat = await client.getChatById(chatId);
        const messages = await chat.fetchMessages({ limit: parseInt(limit) });

        const formattedMessages = messages.map(msg => ({
            id: msg.id._serialized,
            from: msg.from,
            to: msg.to,
            body: msg.body,
            timestamp: msg.timestamp
        }));

        res.json(formattedMessages);
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

// Get all chats
app.get('/chats', async (req, res) => {
    const { sessionId } = req.query;

    if (!sessionId) {
        return res.status(400).json({ error: 'sessionId is required' });
    }

    const client = clients.get(sessionId);
    if (!client) {
        return res.status(404).json({ error: 'Session not found' });
    }

    try {
        const chats = await client.getChats();
        const formattedChats = chats.map(chat => ({
            id: chat.id._serialized,
            name: chat.name,
            isGroup: chat.isGroup,
            unreadCount: chat.unreadCount,
            timestamp: chat.timestamp
        }));

        res.json(formattedChats);
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

// Get contacts
app.get('/contacts', async (req, res) => {
    const { sessionId } = req.query;

    if (!sessionId) {
        return res.status(400).json({ error: 'sessionId is required' });
    }

    const client = clients.get(sessionId);
    if (!client) {
        return res.status(404).json({ error: 'Session not found' });
    }

    try {
        const contacts = await client.getContacts();
        const formattedContacts = contacts.map(contact => ({
            id: contact.id._serialized,
            name: contact.name || contact.pushname,
            number: contact.number,
            isMyContact: contact.isMyContact
        }));

        res.json(formattedContacts);
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

// Check if number is registered on WhatsApp
app.get('/number/check', async (req, res) => {
    const { sessionId, number } = req.query;

    if (!sessionId || !number) {
        return res.status(400).json({ error: 'sessionId and number are required' });
    }

    const client = clients.get(sessionId);
    if (!client) {
        return res.status(404).json({ error: 'Session not found' });
    }

    try {
        const numberId = number.includes('@c.us') ? number : `${number}@c.us`;
        const isRegistered = await client.isRegisteredUser(numberId);
        res.json({ number, isRegistered });
    } catch (error) {
        res.status(500).json({ error: error.message });
    }
});

app.listen(PORT, () => {
    console.log(`WhatsApp Bridge Service running on port ${PORT}`);
});
