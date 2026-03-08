import { useState, useEffect } from 'react';
import { whatsapp, apiConnections } from '../api';
import { WhatsAppSession } from '../types';

export default function WhatsAppSessions() {
  const [sessions, setSessions] = useState<WhatsAppSession[]>([]);
  const [loading, setLoading] = useState(true);
  const [qrCode, setQrCode] = useState<string | null>(null);
  const [currentSessionId, setCurrentSessionId] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [testingId, setTestingId] = useState<number | null>(null);
  const [testResults, setTestResults] = useState<{ [key: number]: { success: boolean; message: string; details?: any } }>({});

  useEffect(() => {
    loadSessions();
  }, []);

  useEffect(() => {
    if (currentSessionId) {
      const interval = setInterval(() => {
        checkQrStatus();
      }, 3000);
      return () => clearInterval(interval);
    }
  }, [currentSessionId]);

  const loadSessions = async () => {
    try {
      const response = await whatsapp.getSessions();
      setSessions(response.data);
    } catch (err) {
      setError('Failed to load WhatsApp sessions');
    } finally {
      setLoading(false);
    }
  };

  const checkQrStatus = async () => {
    if (!currentSessionId) return;

    try {
      const response = await whatsapp.getQr(currentSessionId);
      if (response.data.status === 'connected') {
        setQrCode(null);
        setCurrentSessionId(null);
        loadSessions();
      }
    } catch (err) {
      console.error('Error checking QR status:', err);
    }
  };

  const handleCreateSession = async () => {
    setError('');
    setQrCode(null);

    try {
      const response = await whatsapp.createSession();
      setQrCode(response.data.qrCode);
      setCurrentSessionId(response.data.sessionId);
      loadSessions();
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to create WhatsApp session');
    }
  };

  const handleTestSession = async (sessionId: string, sessionDbId: number) => {
    setTestingId(sessionDbId);
    setTestResults((prev) => {
      const updated = { ...prev };
      delete updated[sessionDbId];
      return updated;
    });

    try {
      const response = await whatsapp.testSession(sessionId);
      setTestResults((prev) => ({
        ...prev,
        [sessionDbId]: {
          success: response.data.success,
          message: response.data.message,
          details: response.data
        },
      }));
      if (response.data.success) {
        loadSessions(); // Refresh to show updated lastSeenAt
      }
    } catch (err: any) {
      setTestResults((prev) => ({
        ...prev,
        [sessionDbId]: {
          success: false,
          message: err.response?.data?.message || 'Failed to test WhatsApp session',
        },
      }));
    } finally {
      setTestingId(null);
    }
  };

  const handleDeleteSession = async (sessionId: string) => {
    if (!confirm('Are you sure you want to disconnect this WhatsApp session?')) return;

    try {
      await whatsapp.deleteSession(sessionId);
      loadSessions();
    } catch (err) {
      setError('Failed to delete WhatsApp session');
    }
  };

  if (loading) {
    return <div className="container">Loading...</div>;
  }

  return (
    <div className="container">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '24px' }}>
        <h2>WhatsApp Sessions</h2>
        <button className="btn btn-primary" onClick={handleCreateSession} disabled={!!qrCode}>
          {qrCode ? 'Connecting...' : 'Connect WhatsApp'}
        </button>
      </div>

      {error && <div className="error" style={{ marginBottom: '16px' }}>{error}</div>}

      {qrCode && (
        <div className="card">
          <div className="qr-container">
            <h3>Scan QR Code with WhatsApp</h3>
            <p style={{ color: '#666', marginBottom: '16px' }}>
              Open WhatsApp on your phone, go to Settings → Linked Devices → Link a Device, and scan this QR code.
            </p>
            <img src={qrCode} alt="QR Code" />
            <p style={{ color: '#666', fontSize: '14px' }}>
              Waiting for connection... This page will automatically update when connected.
            </p>
          </div>
        </div>
      )}

      <div className="card">
        <h3 style={{ marginBottom: '16px' }}>Your WhatsApp Sessions</h3>
        {sessions.length === 0 ? (
          <p style={{ color: '#666' }}>No WhatsApp sessions yet. Connect WhatsApp to get started.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>Phone Number</th>
                <th>Status</th>
                <th>Created</th>
                <th>Connected</th>
                <th>Last Seen</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {sessions.map((session) => (
                <>
                  <tr key={session.id}>
                    <td>{session.phoneNumber || 'N/A'}</td>
                    <td>
                      <span
                        className={`status-badge ${
                          session.status === 'connected'
                            ? 'status-connected'
                            : session.status === 'qr_pending'
                            ? 'status-pending'
                            : 'status-disconnected'
                        }`}
                      >
                        {session.status}
                      </span>
                    </td>
                    <td>{new Date(session.createdAt).toLocaleDateString()}</td>
                    <td>{session.connectedAt ? new Date(session.connectedAt).toLocaleDateString() : 'N/A'}</td>
                    <td>{session.lastSeenAt ? new Date(session.lastSeenAt).toLocaleString() : 'N/A'}</td>
                    <td>
                      <button
                        className="btn btn-secondary"
                        onClick={() => handleTestSession(session.sessionId, session.id)}
                        disabled={testingId === session.id}
                        style={{ marginRight: '8px' }}
                      >
                        {testingId === session.id ? 'Testing...' : 'Test Connection'}
                      </button>
                      <button className="btn btn-danger" onClick={() => handleDeleteSession(session.sessionId)}>
                        Disconnect
                      </button>
                    </td>
                  </tr>
                  {testResults[session.id] && (
                    <tr key={`${session.id}-test-result`}>
                      <td colSpan={6} style={{ padding: '8px 16px' }}>
                        <div
                          style={{
                            padding: '12px',
                            borderRadius: '4px',
                            background: testResults[session.id].success ? '#f0fdf4' : '#fef2f2',
                            border: `1px solid ${testResults[session.id].success ? '#86efac' : '#fca5a5'}`,
                          }}
                        >
                          <strong style={{ color: testResults[session.id].success ? '#166534' : '#991b1b' }}>
                            {testResults[session.id].success ? '✓ Success: ' : '✗ Failed: '}
                          </strong>
                          <span style={{ color: testResults[session.id].success ? '#166534' : '#991b1b' }}>
                            {testResults[session.id].message}
                          </span>
                          {testResults[session.id].success && testResults[session.id].details?.phoneNumber && (
                            <div style={{ marginTop: '8px', fontSize: '14px', color: '#166534' }}>
                              Phone: {testResults[session.id].details.phoneNumber}
                            </div>
                          )}
                        </div>
                      </td>
                    </tr>
                  )}
                </>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="card">
        <h3 style={{ marginBottom: '16px' }}>Security Information</h3>
        <p style={{ marginBottom: '12px', lineHeight: '1.6' }}>
          <strong>Encryption:</strong> This application supports optional encryption for phone numbers, tokens, and messages.
          Check your server configuration to see if encryption is enabled.
        </p>
        <p style={{ lineHeight: '1.6' }}>
          <strong>Data Storage:</strong> All WhatsApp session data is stored securely in the database.
          When encryption is enabled, sensitive data like phone numbers and API tokens are encrypted using AES-256.
        </p>
      </div>
    </div>
  );
}
