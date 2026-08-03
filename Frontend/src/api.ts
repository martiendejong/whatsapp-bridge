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

export const auth = {
  register: (email: string, password: string) =>
    api.post('/api/auth/register', { Email: email, Password: password }),
  login: (email: string, password: string) =>
    api.post('/api/auth/login', { Email: email, Password: password }),
};

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
  getStoredMessageMedia: (sessionId: string, chatJid: string, messageId: string) =>
    api.get(`/api/whatsapp/sessions/${sessionId}/store/messages/media`, {
      params: { chatJid, messageId },
      responseType: 'blob',
    }),
};

export default api;
