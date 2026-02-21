import { useState, useEffect } from 'react';
import { apiConnections } from '../api';
import { ApiConnection } from '../types';

export default function ApiConnections() {
  const [connections, setConnections] = useState<ApiConnection[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [newName, setNewName] = useState('');
  const [newToken, setNewToken] = useState('');
  const [error, setError] = useState('');

  useEffect(() => {
    loadConnections();
  }, []);

  const loadConnections = async () => {
    try {
      const response = await apiConnections.getAll();
      setConnections(response.data);
    } catch (err) {
      setError('Failed to load API connections');
    } finally {
      setLoading(false);
    }
  };

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');

    try {
      const response = await apiConnections.create(newName);
      setNewToken(response.data.token);
      setNewName('');
      loadConnections();
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to create API connection');
    }
  };

  const handleDelete = async (id: number) => {
    if (!confirm('Are you sure you want to delete this API connection?')) return;

    try {
      await apiConnections.delete(id);
      loadConnections();
    } catch (err) {
      setError('Failed to delete API connection');
    }
  };

  const handleToggle = async (id: number) => {
    try {
      await apiConnections.toggle(id);
      loadConnections();
    } catch (err) {
      setError('Failed to toggle API connection');
    }
  };

  const copyToClipboard = (text: string) => {
    navigator.clipboard.writeText(text);
    alert('Token copied to clipboard!');
  };

  if (loading) {
    return <div className="container">Loading...</div>;
  }

  return (
    <div className="container">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '24px' }}>
        <h2>API Connections</h2>
        <button className="btn btn-primary" onClick={() => setShowForm(!showForm)}>
          {showForm ? 'Cancel' : 'Create New Connection'}
        </button>
      </div>

      {error && <div className="error" style={{ marginBottom: '16px' }}>{error}</div>}

      {showForm && (
        <div className="card">
          <h3 style={{ marginBottom: '16px' }}>Create API Connection</h3>
          <form onSubmit={handleCreate}>
            <div className="form-group">
              <label>Connection Name</label>
              <input
                type="text"
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                placeholder="e.g., My App API"
                required
              />
            </div>
            <button type="submit" className="btn btn-primary">
              Create Connection
            </button>
          </form>

          {newToken && (
            <div style={{ marginTop: '24px', padding: '16px', background: '#f0f9ff', borderRadius: '4px' }}>
              <h4 style={{ marginBottom: '12px', color: '#0369a1' }}>API Token Created!</h4>
              <p style={{ marginBottom: '8px', fontSize: '14px' }}>
                Save this token securely. You won't be able to see it again.
              </p>
              <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
                <code style={{ flex: 1, padding: '8px', background: 'white', borderRadius: '4px', fontSize: '13px' }}>
                  {newToken}
                </code>
                <button className="btn btn-secondary" onClick={() => copyToClipboard(newToken)}>
                  Copy
                </button>
              </div>
            </div>
          )}
        </div>
      )}

      <div className="card">
        <h3 style={{ marginBottom: '16px' }}>Your API Connections</h3>
        {connections.length === 0 ? (
          <p style={{ color: '#666' }}>No API connections yet. Create one to get started.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Token (first 20 chars)</th>
                <th>Created</th>
                <th>Last Used</th>
                <th>Status</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {connections.map((conn) => (
                <tr key={conn.id}>
                  <td>{conn.name}</td>
                  <td>
                    <code>{conn.token.substring(0, 20)}...</code>
                  </td>
                  <td>{new Date(conn.createdAt).toLocaleDateString()}</td>
                  <td>{conn.lastUsedAt ? new Date(conn.lastUsedAt).toLocaleDateString() : 'Never'}</td>
                  <td>
                    <span className={`status-badge ${conn.isActive ? 'status-connected' : 'status-disconnected'}`}>
                      {conn.isActive ? 'Active' : 'Inactive'}
                    </span>
                  </td>
                  <td>
                    <button
                      className="btn btn-secondary"
                      onClick={() => handleToggle(conn.id)}
                      style={{ marginRight: '8px' }}
                    >
                      {conn.isActive ? 'Disable' : 'Enable'}
                    </button>
                    <button className="btn btn-danger" onClick={() => handleDelete(conn.id)}>
                      Delete
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
