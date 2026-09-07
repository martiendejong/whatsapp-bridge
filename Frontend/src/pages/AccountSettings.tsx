import { useEffect, useState } from 'react';
import { useAuth } from '../AuthContext';
import api, { admin } from '../api';

interface ActiveSessionEngine {
  sessionId: string;
  engine: string;
  isConnected: boolean;
}

function EngineSettings() {
  const [engine, setEngine] = useState('');
  const [selected, setSelected] = useState('');
  const [availableEngines, setAvailableEngines] = useState<string[]>([]);
  const [activeSessions, setActiveSessions] = useState<ActiveSessionEngine[]>([]);
  const [restartSessions, setRestartSessions] = useState(true);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const load = async () => {
    try {
      const res = await admin.getEngine();
      setEngine(res.data.engine);
      setSelected(res.data.engine);
      setAvailableEngines(res.data.availableEngines || []);
      setActiveSessions(res.data.activeSessions || []);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load engine settings');
    }
  };

  useEffect(() => { load(); }, []);

  const handleSave = async () => {
    setError('');
    setMessage('');
    setLoading(true);
    try {
      const res = await admin.setEngine(selected, restartSessions);
      setMessage(res.data.note || `Engine set to ${res.data.engine}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to switch engine');
    } finally {
      setLoading(false);
    }
  };

  const engineLabel = (e: string) =>
    e === 'dawa' ? 'Dawa (built-in C# client)' : e === 'baileys' ? 'Baileys (Node.js sidecar)' : e;

  return (
    <div className="bg-white shadow rounded-lg p-6">
      <h2 className="text-xl font-semibold mb-1">WhatsApp Engine <span className="text-xs font-normal text-gray-500">(admin)</span></h2>
      <p className="text-sm text-gray-600 mb-4">
        Which client implementation the bridge uses to talk to WhatsApp. Dawa and Baileys store
        credentials separately: the first time a session connects on the other engine it needs a
        new QR scan. Avoid switching back and forth rapidly — each pairing registers a new
        linked device.
      </p>

      {message && (
        <div className="mb-4 p-3 bg-green-100 border border-green-400 text-green-700 rounded text-sm">{message}</div>
      )}
      {error && (
        <div className="mb-4 p-3 bg-red-100 border border-red-400 text-red-700 rounded text-sm">{error}</div>
      )}

      <div className="space-y-3">
        {availableEngines.map((e) => (
          <label key={e} className="flex items-center gap-3 cursor-pointer">
            <input
              type="radio"
              name="engine"
              value={e}
              checked={selected === e}
              onChange={() => setSelected(e)}
            />
            <span className="text-gray-900">{engineLabel(e)}</span>
            {engine === e && (
              <span className="inline-flex px-2 py-0.5 text-xs font-semibold rounded-full bg-blue-100 text-blue-800">current</span>
            )}
          </label>
        ))}

        <label className="flex items-center gap-3 cursor-pointer pt-2">
          <input
            type="checkbox"
            checked={restartSessions}
            onChange={(e) => setRestartSessions(e.target.checked)}
          />
          <span className="text-sm text-gray-700">Reconnect active sessions immediately</span>
        </label>

        <button
          onClick={handleSave}
          disabled={loading || !selected || selected === engine}
          className="px-6 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:bg-gray-400 disabled:cursor-not-allowed transition"
        >
          {loading ? 'Switching...' : 'Switch Engine'}
        </button>
      </div>

      {activeSessions.length > 0 && (
        <div className="mt-6">
          <h3 className="text-sm font-medium text-gray-700 mb-2">Active sessions</h3>
          <ul className="space-y-1">
            {activeSessions.map((s) => (
              <li key={s.sessionId} className="text-sm text-gray-600">
                <span className="font-mono">{s.sessionId.slice(0, 8)}…</span>
                {' · '}{engineLabel(s.engine)}
                {' · '}
                <span className={s.isConnected ? 'text-green-700' : 'text-red-700'}>
                  {s.isConnected ? 'connected' : 'disconnected'}
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

export default function AccountSettings() {
  const { user, login, token } = useAuth();
  const [email, setEmail] = useState(user?.email || '');
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const handleUpdateEmail = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setMessage('');
    setLoading(true);

    try {
      await api.put('/auth/update-email', { email });
      setMessage('Email updated successfully');

      // Update user in context
      if (user) {
        login({ ...user, email }, token!);
      }
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to update email');
    } finally {
      setLoading(false);
    }
  };

  const handleUpdatePassword = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setMessage('');

    if (newPassword !== confirmPassword) {
      setError('New passwords do not match');
      return;
    }

    if (newPassword.length < 6) {
      setError('Password must be at least 6 characters');
      return;
    }

    setLoading(true);

    try {
      await api.put('/auth/update-password', {
        currentPassword,
        newPassword
      });

      setMessage('Password updated successfully');
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to update password');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="max-w-4xl mx-auto p-6">
      <h1 className="text-3xl font-bold mb-8">Account Settings</h1>

      {message && (
        <div className="mb-4 p-4 bg-green-100 border border-green-400 text-green-700 rounded">
          {message}
        </div>
      )}

      {error && (
        <div className="mb-4 p-4 bg-red-100 border border-red-400 text-red-700 rounded">
          {error}
        </div>
      )}

      <div className="space-y-8">
        {/* Account Information */}
        <div className="bg-white shadow rounded-lg p-6">
          <h2 className="text-xl font-semibold mb-4">Account Information</h2>

          <div className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                User ID
              </label>
              <p className="text-gray-900">{user?.id}</p>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Account Created
              </label>
              <p className="text-gray-900">
                {user?.createdAt ? new Date(user.createdAt).toLocaleDateString() : 'N/A'}
              </p>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Last Login
              </label>
              <p className="text-gray-900">
                {user?.lastLoginAt ? new Date(user.lastLoginAt).toLocaleDateString() : 'Never'}
              </p>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Account Status
              </label>
              <span className={`inline-flex px-2 py-1 text-xs font-semibold rounded-full ${
                user?.isActive ? 'bg-green-100 text-green-800' : 'bg-red-100 text-red-800'
              }`}>
                {user?.isActive ? 'Active' : 'Inactive'}
              </span>
            </div>
          </div>
        </div>

        {/* Update Email */}
        <div className="bg-white shadow rounded-lg p-6">
          <h2 className="text-xl font-semibold mb-4">Update Email</h2>

          <form onSubmit={handleUpdateEmail} className="space-y-4">
            <div>
              <label htmlFor="email" className="block text-sm font-medium text-gray-700 mb-1">
                Email Address
              </label>
              <input
                type="email"
                id="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                className="w-full px-4 py-2 border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500"
                required
              />
            </div>

            <button
              type="submit"
              disabled={loading || email === user?.email}
              className="px-6 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:bg-gray-400 disabled:cursor-not-allowed transition"
            >
              {loading ? 'Updating...' : 'Update Email'}
            </button>
          </form>
        </div>

        {/* Update Password */}
        <div className="bg-white shadow rounded-lg p-6">
          <h2 className="text-xl font-semibold mb-4">Change Password</h2>

          <form onSubmit={handleUpdatePassword} className="space-y-4">
            <div>
              <label htmlFor="currentPassword" className="block text-sm font-medium text-gray-700 mb-1">
                Current Password
              </label>
              <input
                type="password"
                id="currentPassword"
                value={currentPassword}
                onChange={(e) => setCurrentPassword(e.target.value)}
                className="w-full px-4 py-2 border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500"
                required
              />
            </div>

            <div>
              <label htmlFor="newPassword" className="block text-sm font-medium text-gray-700 mb-1">
                New Password
              </label>
              <input
                type="password"
                id="newPassword"
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
                className="w-full px-4 py-2 border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500"
                required
                minLength={6}
              />
              <p className="text-xs text-gray-500 mt-1">
                Password must be at least 6 characters
              </p>
            </div>

            <div>
              <label htmlFor="confirmPassword" className="block text-sm font-medium text-gray-700 mb-1">
                Confirm New Password
              </label>
              <input
                type="password"
                id="confirmPassword"
                value={confirmPassword}
                onChange={(e) => setConfirmPassword(e.target.value)}
                className="w-full px-4 py-2 border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500"
                required
                minLength={6}
              />
            </div>

            <button
              type="submit"
              disabled={loading || !currentPassword || !newPassword || !confirmPassword}
              className="px-6 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:bg-gray-400 disabled:cursor-not-allowed transition"
            >
              {loading ? 'Updating...' : 'Change Password'}
            </button>
          </form>
        </div>

        {/* WhatsApp engine (admin only) */}
        {user?.isAdmin && <EngineSettings />}
      </div>
    </div>
  );
}
