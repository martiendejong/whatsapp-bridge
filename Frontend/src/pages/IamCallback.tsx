import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { auth } from '../api';
import { useAuth } from '../AuthContext';

export default function IamCallback() {
  const [searchParams] = useSearchParams();
  const [error, setError] = useState('');
  const { login } = useAuth();
  const navigate = useNavigate();

  useEffect(() => {
    const token = searchParams.get('token');

    if (!token) {
      setError('No token received from IAM');
      return;
    }

    auth.me(token)
      .then((response) => {
        login(response.data.user, token);
        navigate('/dashboard');
      })
      .catch(() => setError('Failed to complete IAM login'));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className="container" style={{ maxWidth: '400px', marginTop: '80px' }}>
      <div className="card">
        {error ? (
          <>
            <div className="error">{error}</div>
            <p style={{ marginTop: '16px', textAlign: 'center' }}>
              <a href="/login">Back to login</a>
            </p>
          </>
        ) : (
          <p style={{ textAlign: 'center' }}>Signing you in…</p>
        )}
      </div>
    </div>
  );
}
