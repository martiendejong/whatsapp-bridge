import { useEffect, useState } from 'react';
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { auth } from '../api';
import { useAuth } from '../AuthContext';

export default function Verify2FA() {
  const [token, setToken] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [infoMessage, setInfoMessage] = useState('');
  const { login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [searchParams] = useSearchParams();

  useEffect(() => {
    // Message passed from Login page
    const state = location.state as { message?: string; twoFactorMethod?: string } | null;
    if (state?.message) {
      setInfoMessage(state.message);
    }

    // Auto-fill token from ?token= query param (email link click)
    const urlToken = searchParams.get('token');
    if (urlToken) {
      setToken(urlToken);
      handleVerify(urlToken);
    }
  }, []);

  const handleVerify = async (overrideToken?: string) => {
    const tokenToVerify = overrideToken ?? token;
    if (!tokenToVerify.trim()) {
      setError('Please enter the verification code.');
      return;
    }

    setError('');
    setLoading(true);

    try {
      const response = await auth.verify2FA(tokenToVerify.trim());
      login(response.data.user, response.data.token);
      navigate('/dashboard');
    } catch (err: any) {
      setError(err.response?.data?.message || 'Invalid or expired verification code.');
    } finally {
      setLoading(false);
    }
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    handleVerify();
  };

  return (
    <div className="container" style={{ maxWidth: '400px', marginTop: '80px' }}>
      <div className="card">
        <h2 style={{ marginBottom: '8px', textAlign: 'center' }}>Two-Factor Verification</h2>
        <p style={{ textAlign: 'center', color: '#666', marginBottom: '24px', fontSize: '14px' }}>
          Enter the 6-digit code sent to your email, or click the link in the email.
        </p>
        {infoMessage && (
          <div style={{ marginBottom: '16px', padding: '12px', background: '#e8f5e9', borderRadius: '4px', color: '#2e7d32', fontSize: '14px' }}>
            {infoMessage}
          </div>
        )}
        <form onSubmit={handleSubmit}>
          <div className="form-group">
            <label>Verification Code</label>
            <input
              type="text"
              value={token}
              onChange={(e) => setToken(e.target.value)}
              placeholder="123456"
              maxLength={6}
              required
              disabled={loading}
              autoFocus
              style={{ letterSpacing: '0.3em', textAlign: 'center', fontSize: '20px' }}
            />
          </div>
          {error && <div className="error">{error}</div>}
          <button
            type="submit"
            className="btn btn-primary"
            style={{ width: '100%', marginTop: '16px' }}
            disabled={loading || token.trim().length === 0}
          >
            {loading ? 'Verifying...' : 'Verify'}
          </button>
        </form>
        <p style={{ marginTop: '16px', textAlign: 'center', fontSize: '13px', color: '#666' }}>
          Didn't receive a code?{' '}
          <a href="/login" style={{ color: '#1976d2' }}>Go back to login</a>
        </p>
      </div>
    </div>
  );
}
