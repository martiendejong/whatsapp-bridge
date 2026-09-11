import { Link } from 'react-router-dom';
import { useAuth } from '../AuthContext';

export default function Navbar() {
  const { user, logout } = useAuth();

  return (
    <div className="navbar">
      <div className="container">
        <h1>WhatsApp Bridge</h1>
        <div style={{ display: 'flex', gap: '16px', alignItems: 'center' }}>
          <Link to="/dashboard" style={{ color: 'white', textDecoration: 'none' }}>
            Dashboard
          </Link>
          <Link to="/api-connections" style={{ color: 'white', textDecoration: 'none' }}>
            API Connections
          </Link>
          <Link to="/whatsapp-sessions" style={{ color: 'white', textDecoration: 'none' }}>
            WhatsApp
          </Link>
          <Link to="/messages" style={{ color: 'white', textDecoration: 'none' }}>
            Berichten
          </Link>
          <Link to="/audit" style={{ color: 'white', textDecoration: 'none' }}>
            Audit log
          </Link>
          {/* Admin-only server-side (403 for everyone else), so showing the link to a
              non-admin only leads them to a page of failing requests. The audit link stays:
              non-admins legitimately see their own rows there. */}
          {user?.isAdmin && (
            <Link to="/routing" style={{ color: 'white', textDecoration: 'none' }}>
              Routing
            </Link>
          )}
          <Link to="/account" style={{ color: 'white', textDecoration: 'none' }}>
            Account
          </Link>
          <span style={{ color: 'white', opacity: 0.8 }}>{user?.email}</span>
          <button className="btn" onClick={logout}>
            Logout
          </button>
        </div>
      </div>
    </div>
  );
}
