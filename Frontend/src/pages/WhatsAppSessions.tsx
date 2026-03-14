import React, { useState, useEffect } from 'react';
import { QRCodeSVG } from 'qrcode.react';
import { whatsapp } from '../api';
import { WhatsAppSession } from '../types';

interface SendForm {
  to: string;
  message: string;
  sending: boolean;
  result: { success: boolean; message: string } | null;
}

export default function WhatsAppSessions() {
  const [sessions, setSessions] = useState<WhatsAppSession[]>([]);
  const [loading, setLoading] = useState(true);
  const [qrCode, setQrCode] = useState<string | null>(null);
  const [currentSessionId, setCurrentSessionId] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [disconnectedAt, setDisconnectedAt] = useState<number | null>(null);
  const [testingId, setTestingId] = useState<number | null>(null);
  const [testResults, setTestResults] = useState<{ [key: number]: { success: boolean; message: string; details?: any } }>({});
  const [sendForms, setSendForms] = useState<{ [key: string]: SendForm }>({});

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
        setDisconnectedAt(null);
        loadSessions();
      } else if (response.data.status === 'failed') {
        setQrCode(null);
        setCurrentSessionId(null);
        setDisconnectedAt(null);
        setError('WhatsApp rejected the connection. This is usually due to rate limiting — please wait a few minutes and try again.');
        loadSessions();
      } else if (response.data.status === 'disconnected') {
        if (!disconnectedAt) {
          setDisconnectedAt(Date.now());
        } else if (Date.now() - disconnectedAt > 15000) {
          setQrCode(null);
          setCurrentSessionId(null);
          setDisconnectedAt(null);
          setError('WhatsApp rejected the connection. This is usually due to rate limiting — please wait a few minutes and try again.');
          loadSessions();
        }
      } else if (response.data.qrCode) {
        setQrCode(response.data.qrCode);
      }
    } catch (err) {
      console.error('Error checking QR status:', err);
    }
  };

  const handleCreateSession = async () => {
    setError('');
    setQrCode(null);
    setDisconnectedAt(null);

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
        loadSessions();
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

  const toggleSendForm = (sessionId: string) => {
    setSendForms((prev) => {
      if (prev[sessionId]) {
        const updated = { ...prev };
        delete updated[sessionId];
        return updated;
      }
      return { ...prev, [sessionId]: { to: '', message: '', sending: false, result: null } };
    });
  };

  const handleSendMessage = async (sessionId: string) => {
    const form = sendForms[sessionId];
    if (!form || !form.to.trim() || !form.message.trim()) return;

    setSendForms((prev) => ({ ...prev, [sessionId]: { ...prev[sessionId], sending: true, result: null } }));

    try {
      await whatsapp.sendMessage(sessionId, form.to.trim(), form.message.trim());
      setSendForms((prev) => ({
        ...prev,
        [sessionId]: { ...prev[sessionId], sending: false, result: { success: true, message: 'Message sent successfully!' } },
      }));
    } catch (err: any) {
      setSendForms((prev) => ({
        ...prev,
        [sessionId]: {
          ...prev[sessionId],
          sending: false,
          result: { success: false, message: err.response?.data?.message || 'Failed to send message' },
        },
      }));
    }
  };

  if (loading) {
    return <div className="container">Loading...</div>;
  }

  return (
    <div className="container">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '24px' }}>
        <h2>WhatsApp Sessions</h2>
        <button className="btn btn-primary" onClick={handleCreateSession} disabled={!!currentSessionId}>
          {currentSessionId ? 'Connecting...' : 'Connect WhatsApp'}
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
            <QRCodeSVG value={qrCode} size={256} />
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
                <React.Fragment key={session.id}>
                  <tr>
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
                      <button
                        className="btn btn-secondary"
                        onClick={() => toggleSendForm(session.sessionId)}
                        style={{ marginRight: '8px' }}
                      >
                        {sendForms[session.sessionId] ? 'Cancel Send' : 'Send Message'}
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
                  {sendForms[session.sessionId] && (
                    <tr key={`${session.id}-send-form`}>
                      <td colSpan={6} style={{ padding: '8px 16px' }}>
                        <div
                          style={{
                            padding: '16px',
                            borderRadius: '4px',
                            background: '#f8fafc',
                            border: '1px solid #e2e8f0',
                          }}
                        >
                          <h4 style={{ marginBottom: '12px', color: '#374151' }}>Send Message</h4>
                          <div style={{ display: 'flex', gap: '12px', alignItems: 'flex-start', flexWrap: 'wrap' }}>
                            <div style={{ flex: '0 0 200px' }}>
                              <label style={{ display: 'block', marginBottom: '4px', fontSize: '13px', color: '#6b7280' }}>
                                To (phone number with country code)
                              </label>
                              <input
                                type="text"
                                placeholder="+31612345678"
                                value={sendForms[session.sessionId].to}
                                onChange={(e) =>
                                  setSendForms((prev) => ({
                                    ...prev,
                                    [session.sessionId]: { ...prev[session.sessionId], to: e.target.value },
                                  }))
                                }
                                style={{
                                  width: '100%',
                                  padding: '8px 12px',
                                  border: '1px solid #d1d5db',
                                  borderRadius: '4px',
                                  fontSize: '14px',
                                }}
                              />
                            </div>
                            <div style={{ flex: '1 1 300px' }}>
                              <label style={{ display: 'block', marginBottom: '4px', fontSize: '13px', color: '#6b7280' }}>
                                Message
                              </label>
                              <textarea
                                placeholder="Type your message..."
                                value={sendForms[session.sessionId].message}
                                onChange={(e) =>
                                  setSendForms((prev) => ({
                                    ...prev,
                                    [session.sessionId]: { ...prev[session.sessionId], message: e.target.value },
                                  }))
                                }
                                rows={3}
                                style={{
                                  width: '100%',
                                  padding: '8px 12px',
                                  border: '1px solid #d1d5db',
                                  borderRadius: '4px',
                                  fontSize: '14px',
                                  resize: 'vertical',
                                }}
                              />
                            </div>
                            <div style={{ flex: '0 0 auto', paddingTop: '22px' }}>
                              <button
                                className="btn btn-primary"
                                onClick={() => handleSendMessage(session.sessionId)}
                                disabled={
                                  sendForms[session.sessionId].sending ||
                                  !sendForms[session.sessionId].to.trim() ||
                                  !sendForms[session.sessionId].message.trim()
                                }
                              >
                                {sendForms[session.sessionId].sending ? 'Sending...' : 'Send'}
                              </button>
                            </div>
                          </div>
                          {sendForms[session.sessionId].result && (
                            <div
                              style={{
                                marginTop: '12px',
                                padding: '10px 12px',
                                borderRadius: '4px',
                                background: sendForms[session.sessionId].result!.success ? '#f0fdf4' : '#fef2f2',
                                border: `1px solid ${sendForms[session.sessionId].result!.success ? '#86efac' : '#fca5a5'}`,
                                color: sendForms[session.sessionId].result!.success ? '#166534' : '#991b1b',
                                fontSize: '14px',
                              }}
                            >
                              {sendForms[session.sessionId].result!.success ? '✓ ' : '✗ '}
                              {sendForms[session.sessionId].result!.message}
                            </div>
                          )}
                        </div>
                      </td>
                    </tr>
                  )}
                </React.Fragment>
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
