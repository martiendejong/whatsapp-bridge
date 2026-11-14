import { useState, useEffect } from 'react';
import { admin, reasonFrom } from '../api';

interface ManagedUser {
  id: number;
  email: string;
  isAdmin: boolean;
  isActive: boolean;
  createdAt: string;
  lastLoginAt?: string;
}

export default function AdminUsers() {
  const [users, setUsers] = useState<ManagedUser[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<number | null>(null);

  useEffect(() => {
    load();
  }, []);

  async function load() {
    try {
      const res = await admin.getUsers();
      setUsers(res.data);
    } catch (e) {
      setError(reasonFrom(e) ?? 'Could not load users');
    } finally {
      setLoading(false);
    }
  }

  async function toggleAdmin(user: ManagedUser) {
    setBusy(user.id);
    try {
      const res = await admin.toggleAdmin(user.id);
      setUsers(us => us.map(u => u.id === user.id ? { ...u, ...res.data } : u));
    } catch (e) {
      alert(reasonFrom(e) ?? 'Failed to update admin status');
    } finally {
      setBusy(null);
    }
  }

  async function toggleActive(user: ManagedUser) {
    setBusy(user.id);
    try {
      const res = await admin.toggleActive(user.id);
      setUsers(us => us.map(u => u.id === user.id ? { ...u, ...res.data } : u));
    } catch (e) {
      alert(reasonFrom(e) ?? 'Failed to update active status');
    } finally {
      setBusy(null);
    }
  }

  if (loading) return <div className="container"><p>Loading...</p></div>;

  return (
    <div className="container" style={{ marginTop: '32px' }}>
      <h2>User Management</h2>
      {error && <p style={{ color: 'red' }}>{error}</p>}
      <table style={{ width: '100%', borderCollapse: 'collapse', marginTop: '16px' }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #ccc', textAlign: 'left' }}>
            <th style={{ padding: '8px' }}>Email</th>
            <th style={{ padding: '8px' }}>Role</th>
            <th style={{ padding: '8px' }}>Status</th>
            <th style={{ padding: '8px' }}>Last login</th>
            <th style={{ padding: '8px' }}>Actions</th>
          </tr>
        </thead>
        <tbody>
          {users.map(u => (
            <tr key={u.id} style={{ borderBottom: '1px solid #eee', opacity: u.isActive ? 1 : 0.5 }}>
              <td style={{ padding: '8px' }}>{u.email}</td>
              <td style={{ padding: '8px' }}>
                <span style={{
                  background: u.isAdmin ? '#7c3aed' : '#6b7280',
                  color: 'white',
                  borderRadius: '4px',
                  padding: '2px 8px',
                  fontSize: '12px',
                }}>
                  {u.isAdmin ? 'Admin' : 'User'}
                </span>
              </td>
              <td style={{ padding: '8px' }}>
                <span style={{
                  background: u.isActive ? '#16a34a' : '#dc2626',
                  color: 'white',
                  borderRadius: '4px',
                  padding: '2px 8px',
                  fontSize: '12px',
                }}>
                  {u.isActive ? 'Active' : 'Inactive'}
                </span>
              </td>
              <td style={{ padding: '8px', fontSize: '13px', color: '#555' }}>
                {u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleString() : '—'}
              </td>
              <td style={{ padding: '8px', display: 'flex', gap: '8px' }}>
                <button
                  className="btn"
                  disabled={busy === u.id}
                  onClick={() => toggleAdmin(u)}
                  style={{ fontSize: '12px', padding: '4px 10px' }}
                >
                  {u.isAdmin ? 'Remove admin' : 'Make admin'}
                </button>
                <button
                  className="btn"
                  disabled={busy === u.id}
                  onClick={() => toggleActive(u)}
                  style={{ fontSize: '12px', padding: '4px 10px', background: u.isActive ? '#dc2626' : '#16a34a' }}
                >
                  {u.isActive ? 'Deactivate' : 'Activate'}
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
