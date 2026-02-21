export interface User {
  id: number;
  email: string;
}

export interface ApiConnection {
  id: number;
  name: string;
  token: string;
  createdAt: string;
  lastUsedAt?: string;
  isActive: boolean;
}

export interface WhatsAppSession {
  id: number;
  sessionId: string;
  phoneNumber: string;
  status: 'connected' | 'disconnected' | 'qr_pending';
  createdAt: string;
  connectedAt?: string;
  lastSeenAt?: string;
}
