import { useEffect, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { auth, iam, API_BASE_URL } from '../api';
import { useAuth } from '../AuthContext';

export default function Login() {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [iamEnabled, setIamEnabled] = useState(false);
  const [twoFactorPending, setTwoFactorPending] = useState(false);
  const [twoFactorMethod, setTwoFactorMethod] = useState('email');
  const [twoFactorCode, setTwoFactorCode] = useState('');
  const { login } = useAuth();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();

  useEffect(() => {
    iam.status()
      .then((res) => setIamEnabled(!!res.data.enabled))
      .catch(() => setIamEnabled(false));

    const iamError = searchParams.get('iam_error');
    if (iamError === 'two_factor_required') {
      setError('This account has two-factor authentication enabled. Please log in with your password instead.');
    } else if (iamError) {
      setError(`IAM login failed: ${iamError}`);
    }
  }, [searchParams]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      const response = await auth.login(email, password);
      if (response.data.requiresTwoFactor) {
        setTwoFactorPending(true);
        setTwoFactorMethod(response.data.twoFactorMethod || 'email');
      } else {
        login(response.data.user, response.data.token);
        navigate('/dashboard');
      }
    } catch (err: any) {
      setError(err.response?.data?.message || 'Login failed');
    } finally {
      setLoading(false);
    }
  };

  const handleVerify2FA = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      const response = await auth.verify2fa(twoFactorCode);
      login(response.data.user, response.data.token);
      navigate('/dashboard');
    } catch (err: any) {
      setError(err.response?.data?.message || 'Invalid or expired verification code');
    } finally {
      setLoading(false);
    }
  };

  if (twoFactorPending) {
    return (
      <div className="container" style={{ maxWidth: '400px', marginTop: '80px' }}>
        <div className="card">
          <h2 style={{ marginBottom: '8px', textAlign: 'center' }}>Verify Your Identity</h2>
          <p style={{ marginBottom: '24px', textAlign: 'center', color: '#666', fontSize: '14px' }}>
            {twoFactorMethod === 'whatsapp'
              ? 'A verification code has been sent to your WhatsApp.'
              : `A verification code has been sent to ${email}.`}
          </p>
          <form onSubmit={handleVerify2FA}>
            <div className="form-group">
              <label>Verification Code</label>
              <input
                type="text"
                value={twoFactorCode}
                onChange={(e) => setTwoFactorCode(e.target.value)}
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
              disabled={loading || twoFactorCode.length < 6}
            >
              {loading ? 'Verifying...' : 'Verify'}
            </button>
          </form>
          <button
            onClick={() => { setTwoFactorPending(false); setTwoFactorCode(''); setError(''); }}
            style={{ marginTop: '12px', width: '100%', background: 'none', border: 'none', color: '#666', cursor: 'pointer', fontSize: '14px' }}
          >
            Back to login
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="container" style={{ maxWidth: '400px', marginTop: '80px' }}>
      <div className="card">
        <h2 style={{ marginBottom: '24px', textAlign: 'center' }}>Login to WhatsApp Bridge</h2>
        <form onSubmit={handleSubmit}>
          <div className="form-group">
            <label>Email</label>
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
              disabled={loading}
            />
          </div>
          <div className="form-group">
            <label>Password</label>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              disabled={loading}
            />
          </div>
          {error && <div className="error">{error}</div>}
          <button type="submit" className="btn btn-primary" style={{ width: '100%', marginTop: '16px' }} disabled={loading}>
            {loading ? 'Logging in...' : 'Login'}
          </button>
        </form>
        {iamEnabled && (
          <a
            href={`${API_BASE_URL}/api/auth/iam/login`}
            className="btn btn-primary"
            style={{ width: '100%', marginTop: '12px', display: 'block', textAlign: 'center', textDecoration: 'none' }}
          >
            Login with IAM
          </a>
        )}
        <p style={{ marginTop: '16px', textAlign: 'center' }}>
          Don't have an account? <Link to="/register">Register</Link>
        </p>
      </div>
    </div>
  );
}
