import { Link } from 'react-router-dom';

export default function Dashboard() {
  return (
    <div className="container">
      <h2 style={{ marginBottom: '24px' }}>Dashboard</h2>

      <div className="card">
        <h3 style={{ marginBottom: '16px' }}>Welcome to WhatsApp Bridge</h3>
        <p style={{ marginBottom: '16px' }}>
          WhatsApp Bridge allows you to integrate WhatsApp messaging into your applications via a simple API.
        </p>

        <h4 style={{ marginTop: '24px', marginBottom: '12px' }}>Getting Started</h4>
        <ol style={{ paddingLeft: '20px', lineHeight: '1.8' }}>
          <li>
            <Link to="/api-connections">Create an API connection</Link> to get your API token
          </li>
          <li>
            <Link to="/whatsapp-sessions">Connect your WhatsApp</Link> by scanning the QR code
          </li>
          <li>Start sending messages using the API endpoints</li>
        </ol>
      </div>

      <div className="card">
        <h3 style={{ marginBottom: '16px' }}>API Documentation</h3>
        <p style={{ marginBottom: '12px' }}>Base URL: <code>http://your-server:5000/api/wa</code></p>
        <p style={{ marginBottom: '12px' }}>Authorization: <code>Bearer YOUR_API_TOKEN</code></p>

        <h4 style={{ marginTop: '20px', marginBottom: '12px' }}>Available Endpoints:</h4>
        <ul style={{ paddingLeft: '20px', lineHeight: '2' }}>
          <li><code>POST /api/wa/sendMessage</code> - Send text message</li>
          <li><code>POST /api/wa/sendMedia</code> - Send media (image, video, document)</li>
          <li><code>GET /api/wa/getMessages</code> - Get messages from a chat</li>
          <li><code>GET /api/wa/getChats</code> - Get all chats</li>
          <li><code>GET /api/wa/getContacts</code> - Get contacts</li>
          <li><code>GET /api/wa/checkNumberStatus</code> - Check if number is on WhatsApp</li>
        </ul>

        <h4 style={{ marginTop: '20px', marginBottom: '12px' }}>Example Request:</h4>
        <pre style={{ background: '#f5f5f5', padding: '16px', borderRadius: '4px', overflow: 'auto' }}>
{`curl -X POST http://your-server:5000/api/wa/sendMessage \\
  -H "Authorization: Bearer YOUR_API_TOKEN" \\
  -H "Content-Type: application/json" \\
  -d '{
    "to": "1234567890",
    "body": "Hello from WhatsApp Bridge!"
  }'`}
        </pre>
      </div>
    </div>
  );
}
