import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams, useLocation } from 'react-router-dom';
import { auth } from '../api';
import { useAuth } from '../AuthContext';

export default function Verify2FA() {
  const [token, setToken] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const { login } = useAuth();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const { method, message: deliveryMessage } = (location.state as any) || {};

  useEffect(() => {
    const urlToken = searchParams.get('token');
    if (urlToken) {
      verify(urlToken);
    }
  }, []);

  const verify = async (code: string) => {
    setError('');
    setLoading(true);
    try {
      const response = await auth.verify2fa(code);
      login(response.data.user, response.data.token);
      navigate('/dashboard');
    } catch (err: any) {
      setError(err.response?.data?.message || 'Invalid or expired verification code');
      setLoading(false);
    }
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (token.trim()) verify(token.trim());
  };

  return (
    <div className="container" style={{ maxWidth: '400px', marginTop: '80px' }}>
      <div className="card">
        <h2 style={{ marginBottom: '8px', textAlign: 'center' }}>Verify Login</h2>
        <p style={{ textAlign: 'center', color: '#666', marginBottom: '24px' }}>
          {deliveryMessage || (method === 'whatsapp'
            ? 'Enter the 6-digit code sent to your WhatsApp.'
            : method === 'email'
            ? 'Enter the 6-digit code sent to your email.'
            : 'Enter the 6-digit code sent to your WhatsApp or email.')}
        </p>
        <form onSubmit={handleSubmit}>
          <div className="form-group">
            <label>Verification Code</label>
            <input
              type="text"
              value={token}
              onChange={(e) => setToken(e.target.value)}
              placeholder="123456"
              maxLength={6}
              autoFocus
              disabled={loading}
              style={{ textAlign: 'center', letterSpacing: '0.3em', fontSize: '1.5em' }}
            />
          </div>
          {error && <div className="error">{error}</div>}
          <button
            type="submit"
            className="btn btn-primary"
            style={{ width: '100%', marginTop: '16px' }}
            disabled={loading || token.length < 6}
          >
            {loading ? 'Verifying...' : 'Verify'}
          </button>
        </form>
        <p style={{ marginTop: '16px', textAlign: 'center', fontSize: '0.85em', color: '#666' }}>
          Didn't receive a code?{' '}
          <a href="/login" style={{ color: '#3b82f6' }}>Back to Login</a>
        </p>
      </div>
    </div>
  );
}
