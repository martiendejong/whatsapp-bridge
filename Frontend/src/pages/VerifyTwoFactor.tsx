import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { auth } from '../api';
import { useAuth } from '../AuthContext';

export default function VerifyTwoFactor() {
  const [searchParams] = useSearchParams();
  const [status, setStatus] = useState<'verifying' | 'success' | 'error' | 'manual'>('manual');
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const { login } = useAuth();
  const navigate = useNavigate();

  const tokenFromUrl = searchParams.get('token');

  useEffect(() => {
    if (tokenFromUrl) {
      setStatus('verifying');
      auth.verify2fa(tokenFromUrl)
        .then((response) => {
          login(response.data.user, response.data.token);
          setStatus('success');
          setTimeout(() => navigate('/dashboard'), 1500);
        })
        .catch(() => {
          setStatus('error');
          setError('This verification link is invalid or has expired.');
        });
    }
  }, [tokenFromUrl]);

  const handleManualVerify = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      const response = await auth.verify2fa(code);
      login(response.data.user, response.data.token);
      navigate('/dashboard');
    } catch (err: any) {
      setError(err.response?.data?.message || 'Invalid or expired verification code');
    } finally {
      setLoading(false);
    }
  };

  if (status === 'verifying') {
    return (
      <div className="container" style={{ maxWidth: '400px', marginTop: '80px' }}>
        <div className="card" style={{ textAlign: 'center' }}>
          <p>Verifying your login...</p>
        </div>
      </div>
    );
  }

  if (status === 'success') {
    return (
      <div className="container" style={{ maxWidth: '400px', marginTop: '80px' }}>
        <div className="card" style={{ textAlign: 'center' }}>
          <p style={{ color: '#16a34a', fontWeight: 600 }}>Verified! Redirecting...</p>
        </div>
      </div>
    );
  }

  return (
    <div className="container" style={{ maxWidth: '400px', marginTop: '80px' }}>
      <div className="card">
        <h2 style={{ marginBottom: '8px', textAlign: 'center' }}>Enter Verification Code</h2>
        <p style={{ marginBottom: '24px', textAlign: 'center', color: '#666', fontSize: '14px' }}>
          {status === 'error'
            ? 'The link has expired. Enter your code manually below.'
            : 'Enter the 6-digit code from your email.'}
        </p>
        <form onSubmit={handleManualVerify}>
          <div className="form-group">
            <label>Verification Code</label>
            <input
              type="text"
              value={code}
              onChange={(e) => setCode(e.target.value)}
              placeholder="Enter 6-digit code"
              required
              autoFocus
              disabled={loading}
              maxLength={6}
              style={{ letterSpacing: '4px', fontSize: '20px', textAlign: 'center' }}
            />
          </div>
          {error && <div className="error">{error}</div>}
          <button
            type="submit"
            className="btn btn-primary"
            style={{ width: '100%', marginTop: '16px' }}
            disabled={loading || code.length < 6}
          >
            {loading ? 'Verifying...' : 'Verify'}
          </button>
        </form>
      </div>
    </div>
  );
}
