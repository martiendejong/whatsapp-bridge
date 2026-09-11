import axios from 'axios';

const API_BASE_URL = (import.meta as any).env?.VITE_API_URL || '';

const api = axios.create({
  baseURL: API_BASE_URL,
  headers: {
    'Content-Type': 'application/json',
  },
});

// Add token to requests
api.interceptors.request.use((config) => {
  const token = localStorage.getItem('token');
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

// Expired/invalid token: the SPA only checks that a token EXISTS, so a stale JWT left the UI
// "logged in" while every call 401'd (symptom: 'Kon sessies niet laden' on /messages).
// On any 401 outside the auth endpoints: drop the token and force a fresh login.
api.interceptors.response.use(
  (response) => response,
  (error) => {
    const status = error?.response?.status;
    const url: string = error?.config?.url ?? '';
    if (status === 401 && !url.includes('/api/auth/')) {
      localStorage.removeItem('token');
      localStorage.removeItem('user');
      if (!window.location.pathname.startsWith('/login')) {
        window.location.href = '/login';
      }
    }
    return Promise.reject(error);
  }
);

/**
 * The server's own explanation of a failure, or null if it did not give one.
 *
 * The guardrail answers a refusal with { error, blocked: true }, and that string is the whole
 * value of the feature: "outside Sjoerd's window, 23:12 Europe/Amsterdam" tells the operator to
 * wait until morning, where a generic "mislukt" tells them nothing and invites a retry loop.
 * One implementation, used by every page that sends, rather than a copy per catch block.
 */
export function reasonFrom(e: unknown): string | null {
  const data = (e as any)?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;
  return data?.error ?? data?.title ?? null;
}

export const auth = {
  register: (email: string, password: string) =>
    api.post('/api/auth/register', { Email: email, Password: password }),
  login: (email: string, password: string) =>
    api.post('/api/auth/login', { Email: email, Password: password }),
  me: (token: string) =>
    api.get('/api/auth/me', { headers: { Authorization: `Bearer ${token}` } }),
};

export const iam = {
  status: () => api.get('/api/auth/iam/status'),
};

export { API_BASE_URL };

export const apiConnections = {
  getAll: () => api.get('/api/apiconnections'),
  create: (name: string) => api.post('/api/apiconnections', { name }),
  delete: (id: number) => api.delete(`/api/apiconnections/${id}`),
  toggle: (id: number) => api.patch(`/api/apiconnections/${id}/toggle`),
  test: (id: number) => api.post(`/api/apiconnections/${id}/test`),
};

export const whatsapp = {
  getSessions: () => api.get('/api/whatsapp/sessions'),
  createSession: () => api.post('/api/whatsapp/sessions/create'),
  getQr: (sessionId: string) => api.get(`/api/whatsapp/sessions/${sessionId}/qr`),
  deleteSession: (sessionId: string) => api.delete(`/api/whatsapp/sessions/${sessionId}`),
  testSession: (sessionId: string) => api.post(`/api/whatsapp/sessions/${sessionId}/test`),
  // No Category on purpose. Dashboard sends are made by a person, and the backend exempts
  // JWT-authenticated session sends from the routing policy entirely — routing exists to stop
  // AUTOMATED senders waking people, and running human chats through it made this screen unable
  // to reach group chats, customers, or any team member the moment routing was armed.
  sendMessage: (sessionId: string, to: string, message: string) =>
    api.post(`/api/whatsapp/sessions/${sessionId}/send`, { To: to, Message: message }),
  getContacts: (sessionId: string) =>
    api.get(`/api/whatsapp/sessions/${sessionId}/contacts`),
  getStoredChats: (sessionId: string) =>
    api.get(`/api/whatsapp/sessions/${sessionId}/store/chats`),
  getStoredMessages: (sessionId: string, chatJid: string, opts?: { since?: number; before?: number; count?: number }) =>
    api.get(`/api/whatsapp/sessions/${sessionId}/store/messages`, {
      params: { chatJid, ...opts },
    }),
  requestHistory: (sessionId: string, chatJid: string, count = 100) =>
    api.post(`/api/whatsapp/sessions/${sessionId}/request-history`, { ChatJid: chatJid, Count: count }),
  setContactName: (jid: string, name: string) =>
    api.put('/api/whatsapp/contacts/name', { Jid: jid, Name: name }),
  getStoredMessageMedia: (sessionId: string, chatJid: string, messageId: string) =>
    api.get(`/api/whatsapp/sessions/${sessionId}/store/messages/media`, {
      params: { chatJid, messageId },
      responseType: 'blob',
    }),
};

export interface AuditFilters {
  phone?: string;
  eventType?: string;
  outcome?: string;
  apiConnectionId?: number;
  page?: number;
  pageSize?: number;
}

export const audit = {
  list: (filters: AuditFilters = {}) => api.get('/api/wa/audit', { params: filters }),
  phones: () => api.get('/api/wa/audit/phones'),
  eventTypes: () => api.get('/api/wa/audit/event-types'),
  detail: (id: number) => api.get(`/api/wa/audit/${id}`),
};

export interface RoutingContact {
  id: number;
  phone: string;
  name: string;
  alias: string | null;
  enabled: boolean;
  timeZoneId: string;
  windowStartHour: number;
  windowEndHour: number;
  categories: string;
  fallbackPhone: string | null;
  updatedAtUtc: string;
  openNow: boolean;
}

export const routing = {
  list: () => api.get<RoutingContact[]>('/api/wa/routing'),
  save: (contact: Omit<RoutingContact, 'id' | 'updatedAtUtc' | 'openNow'>) =>
    api.post('/api/wa/routing', contact),
  remove: (id: number) => api.delete(`/api/wa/routing/${id}`),
  preview: (to: string, category?: string) =>
    api.get('/api/wa/routing/preview', { params: { to, category: category || undefined } }),
  timezones: () => api.get<string[]>('/api/wa/routing/timezones'),
};

export const admin = {
  getEngine: () => api.get('/api/admin/engine'),
  setEngine: (engine: string, restartSessions: boolean) =>
    api.put('/api/admin/engine', { engine, restartSessions }),
};

export default api;
