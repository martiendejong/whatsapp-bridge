import { useState, useEffect, useRef, useCallback } from 'react';
import { whatsapp } from '../api';
import { WhatsAppSession } from '../types';

interface StoredChat {
  chatJid: string;
  phone: string;
  name: string | null;
  messageCount: number;
  lastTimestamp: number;
  lastBody: string | null;
  lastFromMe: boolean;
}

interface StoredMessage {
  id: string;
  chatJid: string;
  fromMe: boolean;
  sender: string;
  body: string;
  type: string;
  mediaUrl: string | null;
  timestamp: number;
  isHistory: boolean;
}

function formatTime(unixSeconds: number): string {
  const d = new Date(unixSeconds * 1000);
  const now = new Date();
  const sameDay = d.toDateString() === now.toDateString();
  const time = d.toLocaleTimeString('nl-NL', { hour: '2-digit', minute: '2-digit' });
  return sameDay ? time : `${d.toLocaleDateString('nl-NL', { day: '2-digit', month: '2-digit' })} ${time}`;
}

export default function Messages() {
  const [sessions, setSessions] = useState<WhatsAppSession[]>([]);
  const [sessionId, setSessionId] = useState<string>('');
  const [chats, setChats] = useState<StoredChat[]>([]);
  const [selectedChat, setSelectedChat] = useState<string>('');
  const [messages, setMessages] = useState<StoredMessage[]>([]);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState('');
  const threadRef = useRef<HTMLDivElement>(null);
  const selectedChatRef = useRef(selectedChat);
  selectedChatRef.current = selectedChat;

  useEffect(() => {
    whatsapp.getSessions().then((res) => {
      const all: WhatsAppSession[] = res.data;
      setSessions(all);
      const preferred = all.find((s) => s.status === 'connected') ?? all[0];
      if (preferred) setSessionId(preferred.sessionId);
    }).catch(() => setError('Kon sessies niet laden'));
  }, []);

  const loadChats = useCallback(async (sid: string) => {
    try {
      const res = await whatsapp.getStoredChats(sid);
      setChats(res.data);
      setError('');
    } catch {
      setError('Kon chats niet laden');
    }
  }, []);

  const loadMessages = useCallback(async (sid: string, chatJid: string) => {
    try {
      const res = await whatsapp.getStoredMessages(sid, chatJid, { count: 200 });
      // Ignore a response that arrives after the user switched chats
      if (selectedChatRef.current === chatJid) setMessages(res.data);
    } catch {
      /* keep last known thread on a failed poll */
    }
  }, []);

  // chat list: load + poll
  useEffect(() => {
    if (!sessionId) return;
    loadChats(sessionId);
    const iv = setInterval(() => loadChats(sessionId), 10000);
    return () => clearInterval(iv);
  }, [sessionId, loadChats]);

  // thread: load + poll
  useEffect(() => {
    if (!sessionId || !selectedChat) return;
    setMessages([]);
    loadMessages(sessionId, selectedChat);
    const iv = setInterval(() => loadMessages(sessionId, selectedChat), 5000);
    return () => clearInterval(iv);
  }, [sessionId, selectedChat, loadMessages]);

  // autoscroll on new messages
  useEffect(() => {
    threadRef.current?.scrollTo({ top: threadRef.current.scrollHeight });
  }, [messages.length, selectedChat]);

  const send = async () => {
    if (!draft.trim() || !sessionId || !selectedChat || sending) return;
    setSending(true);
    try {
      await whatsapp.sendMessage(sessionId, selectedChat.split('@')[0], draft.trim());
      setDraft('');
      // the durable store mirrors sends; refresh soon after
      setTimeout(() => loadMessages(sessionId, selectedChat), 1500);
    } catch {
      setError('Versturen mislukt');
    } finally {
      setSending(false);
    }
  };

  const activeChat = chats.find((c) => c.chatJid === selectedChat);

  return (
    <div className="container" style={{ paddingTop: 16 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 12 }}>
        <h2 style={{ margin: 0 }}>Berichten</h2>
        <select
          value={sessionId}
          onChange={(e) => { setSessionId(e.target.value); setSelectedChat(''); }}
          style={{ padding: '6px 10px', borderRadius: 6 }}
        >
          {sessions.map((s) => (
            <option key={s.sessionId} value={s.sessionId}>
              {s.phoneNumber || s.sessionId.slice(0, 8)} ({s.status})
            </option>
          ))}
        </select>
        {error && <span style={{ color: '#c0392b' }}>{error}</span>}
      </div>

      <div style={{
        display: 'flex', height: 'calc(100vh - 160px)', minHeight: 420,
        border: '1px solid #ddd', borderRadius: 8, overflow: 'hidden', background: '#fff',
      }}>
        {/* chat list */}
        <div style={{ width: 320, minWidth: 240, borderRight: '1px solid #ddd', overflowY: 'auto', background: '#fafafa' }}>
          {chats.length === 0 && (
            <div style={{ padding: 16, color: '#888' }}>
              Nog geen opgeslagen gesprekken voor dit account.
            </div>
          )}
          {chats.map((c) => (
            <div
              key={c.chatJid}
              onClick={() => setSelectedChat(c.chatJid)}
              style={{
                padding: '10px 14px', cursor: 'pointer',
                borderBottom: '1px solid #eee',
                background: c.chatJid === selectedChat ? '#e8f5e9' : 'transparent',
              }}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8 }}>
                <strong style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {c.name || c.phone}
                </strong>
                <span style={{ color: '#888', fontSize: 12, flexShrink: 0 }}>{formatTime(c.lastTimestamp)}</span>
              </div>
              <div style={{ color: '#666', fontSize: 13, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {c.lastFromMe ? 'Jij: ' : ''}{c.lastBody || `(${c.messageCount} berichten)`}
              </div>
            </div>
          ))}
        </div>

        {/* thread */}
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', background: '#efeae2' }}>
          {!selectedChat ? (
            <div style={{ margin: 'auto', color: '#888' }}>Kies een gesprek om de berichten te zien</div>
          ) : (
            <>
              <div style={{ padding: '10px 16px', background: '#f0f0f0', borderBottom: '1px solid #ddd', fontWeight: 600 }}>
                {activeChat?.name || activeChat?.phone || selectedChat}
                <span style={{ fontWeight: 400, color: '#888', marginLeft: 8, fontSize: 13 }}>
                  {activeChat ? `${activeChat.messageCount} berichten` : ''}
                </span>
              </div>
              <div ref={threadRef} style={{ flex: 1, overflowY: 'auto', padding: 16 }}>
                {messages.map((m, i) => (
                  <div key={`${m.id}-${i}`} style={{ display: 'flex', justifyContent: m.fromMe ? 'flex-end' : 'flex-start', marginBottom: 6 }}>
                    <div style={{
                      maxWidth: '70%', padding: '8px 12px', borderRadius: 8,
                      background: m.fromMe ? '#d9fdd3' : '#fff',
                      boxShadow: '0 1px 1px rgba(0,0,0,0.1)',
                      whiteSpace: 'pre-wrap', wordBreak: 'break-word', fontSize: 14,
                    }}>
                      {m.body || (m.mediaUrl ? `[${m.type}]` : '')}
                      {m.mediaUrl && (
                        <div><a href={m.mediaUrl} target="_blank" rel="noreferrer">media</a></div>
                      )}
                      <div style={{ fontSize: 11, color: '#8a8a8a', textAlign: 'right', marginTop: 2 }}>
                        {m.isHistory ? 'hist · ' : ''}{formatTime(m.timestamp)}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
              <div style={{ display: 'flex', gap: 8, padding: 10, background: '#f0f0f0' }}>
                <input
                  style={{ flex: 1, padding: '10px 12px', borderRadius: 20, border: '1px solid #ccc' }}
                  placeholder="Typ een bericht"
                  value={draft}
                  onChange={(e) => setDraft(e.target.value)}
                  onKeyDown={(e) => { if (e.key === 'Enter') send(); }}
                />
                <button className="btn" onClick={send} disabled={sending || !draft.trim()}>
                  {sending ? '...' : 'Verstuur'}
                </button>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
