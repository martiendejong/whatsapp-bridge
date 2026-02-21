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
          <span style={{ color: 'white', opacity: 0.8 }}>{user?.email}</span>
          <button className="btn" onClick={logout}>
            Logout
          </button>
        </div>
      </div>
    </div>
  );
}
