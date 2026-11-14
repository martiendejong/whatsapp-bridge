import { useState, useEffect, useCallback, Fragment } from 'react';
import { audit } from '../api';

interface AuditEntry {
  id: number;
  atUtc: string;
  method: string;
  path: string;
  eventType: string;
  phone: string | null;
  body: string | null;
  responsePreview: string | null;
  statusCode: number;
  outcome: string;
  durationMs: number;
  apiConnectionId: number | null;
  apiConnectionName: string | null;
  authScheme: string;
  clientIp: string | null;
}

interface PhoneRow {
  phone: string;
  total: number;
  sent: number;
  blocked: number;
  errors: number;
  lastAtUtc: string;
}

interface EventTypeRow {
  eventType: string;
  count: number;
}

const PAGE_SIZE = 50;

const outcomeColor = (outcome: string) =>
  outcome === 'blocked' ? '#b45309' : outcome === 'error' ? '#b91c1c' : '#15803d';

export default function AuditLog() {
  const [entries, setEntries] = useState<AuditEntry[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const [phones, setPhones] = useState<PhoneRow[]>([]);
  const [eventTypes, setEventTypes] = useState<EventTypeRow[]>([]);

  const [phoneFilter, setPhoneFilter] = useState('');
  const [eventTypeFilter, setEventTypeFilter] = useState('');
  const [outcomeFilter, setOutcomeFilter] = useState('');
  const [expanded, setExpanded] = useState<number | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const response = await audit.list({
        phone: phoneFilter || undefined,
        eventType: eventTypeFilter || undefined,
        outcome: outcomeFilter || undefined,
        page,
        pageSize: PAGE_SIZE,
      });
      setEntries(response.data.items);
      setTotal(response.data.total);
    } catch {
      setError('Kon de audit log niet laden.');
    } finally {
      setLoading(false);
    }
  }, [phoneFilter, eventTypeFilter, outcomeFilter, page]);

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    audit.phones().then((r) => setPhones(r.data)).catch(() => undefined);
    audit.eventTypes().then((r) => setEventTypes(r.data)).catch(() => undefined);
  }, []);

  // Any filter change restarts paging — otherwise you land on page 4 of a 1-page result.
  const applyFilter = (setter: (v: string) => void) => (value: string) => {
    setter(value);
    setPage(1);
  };

  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <div className="container" style={{ paddingTop: 24, paddingBottom: 48 }}>
      <h2>Audit log</h2>
      <p style={{ color: '#555', marginTop: 4 }}>
        Elk API-request dat de bridge bereikt, met de API-sleutel die het maakte en de
        volledige tekst van wat er verstuurd is.
      </p>

      {error && <div className="alert alert-error">{error}</div>}

      {/* ── Per nummer ─────────────────────────────────────────────────────────── */}
      <div className="card" style={{ marginTop: 16 }}>
        <h3 style={{ marginTop: 0 }}>Per nummer</h3>
        {phones.length === 0 ? (
          <p style={{ color: '#777' }}>Nog geen verkeer geregistreerd.</p>
        ) : (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
            {phones.map((p) => (
              <button
                key={p.phone}
                className="btn"
                onClick={() => applyFilter(setPhoneFilter)(p.phone === phoneFilter ? '' : p.phone)}
                style={{
                  border: p.phone === phoneFilter ? '2px solid #2563eb' : '1px solid #ccc',
                  background: p.phone === phoneFilter ? '#eff6ff' : 'white',
                  color: '#111',
                  textAlign: 'left',
                  padding: '8px 12px',
                }}
                title={`Laatst: ${new Date(p.lastAtUtc).toLocaleString('nl-NL')}`}
              >
                <div style={{ fontWeight: 600 }}>{p.phone}</div>
                <div style={{ fontSize: 12, color: '#555' }}>
                  {p.total} requests
                  {p.blocked > 0 && <span style={{ color: '#b45309' }}> · {p.blocked} geblokkeerd</span>}
                  {p.errors > 0 && <span style={{ color: '#b91c1c' }}> · {p.errors} fout</span>}
                </div>
              </button>
            ))}
          </div>
        )}
      </div>

      {/* ── Filters ────────────────────────────────────────────────────────────── */}
      <div className="card" style={{ marginTop: 16, display: 'flex', gap: 16, flexWrap: 'wrap', alignItems: 'flex-end' }}>
        <div>
          <label style={{ display: 'block', fontSize: 13, marginBottom: 4 }}>Telefoonnummer</label>
          <input
            value={phoneFilter}
            onChange={(e) => applyFilter(setPhoneFilter)(e.target.value)}
            placeholder="bijv. 31633984381"
            style={{ padding: 8, minWidth: 200 }}
          />
        </div>

        <div>
          <label style={{ display: 'block', fontSize: 13, marginBottom: 4 }}>Event type</label>
          <select
            value={eventTypeFilter}
            onChange={(e) => applyFilter(setEventTypeFilter)(e.target.value)}
            style={{ padding: 8, minWidth: 200 }}
          >
            <option value="">Alle</option>
            {eventTypes.map((t) => (
              <option key={t.eventType} value={t.eventType}>
                {t.eventType} ({t.count})
              </option>
            ))}
          </select>
        </div>

        <div>
          <label style={{ display: 'block', fontSize: 13, marginBottom: 4 }}>Resultaat</label>
          <select
            value={outcomeFilter}
            onChange={(e) => applyFilter(setOutcomeFilter)(e.target.value)}
            style={{ padding: 8, minWidth: 160 }}
          >
            <option value="">Alle</option>
            <option value="ok">Verstuurd</option>
            <option value="blocked">Geblokkeerd</option>
            <option value="error">Fout</option>
          </select>
        </div>

        {(phoneFilter || eventTypeFilter || outcomeFilter) && (
          <button
            className="btn"
            onClick={() => {
              setPhoneFilter('');
              setEventTypeFilter('');
              setOutcomeFilter('');
              setPage(1);
            }}
          >
            Filters wissen
          </button>
        )}

        <div style={{ marginLeft: 'auto', color: '#555', fontSize: 13 }}>
          {total} resultaten
        </div>
      </div>

      {/* ── Resultaten ─────────────────────────────────────────────────────────── */}
      <div className="card" style={{ marginTop: 16 }}>
        {loading ? (
          <p>Laden...</p>
        ) : entries.length === 0 ? (
          <p style={{ color: '#777' }}>Geen requests gevonden met deze filters.</p>
        ) : (
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
            <thead>
              <tr style={{ textAlign: 'left', borderBottom: '2px solid #e5e7eb' }}>
                <th style={{ padding: '8px 6px' }}>Tijd (UTC)</th>
                <th style={{ padding: '8px 6px' }}>Event</th>
                <th style={{ padding: '8px 6px' }}>Nummer</th>
                <th style={{ padding: '8px 6px' }}>Bericht</th>
                <th style={{ padding: '8px 6px' }}>API-sleutel</th>
                <th style={{ padding: '8px 6px' }}>Resultaat</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((e) => (
                <Fragment key={e.id}>
                  <tr
                    onClick={() => setExpanded(expanded === e.id ? null : e.id)}
                    style={{ borderBottom: '1px solid #f1f5f9', cursor: 'pointer' }}
                  >
                    <td style={{ padding: '8px 6px', whiteSpace: 'nowrap' }}>
                      {new Date(e.atUtc).toLocaleString('nl-NL')}
                    </td>
                    <td style={{ padding: '8px 6px' }}>
                      <code>{e.eventType}</code>
                    </td>
                    <td style={{ padding: '8px 6px' }}>{e.phone || '—'}</td>
                    <td style={{ padding: '8px 6px', maxWidth: 340, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {e.body || <span style={{ color: '#aaa' }}>—</span>}
                    </td>
                    <td style={{ padding: '8px 6px' }}>
                      {e.apiConnectionName || <span style={{ color: '#aaa' }}>{e.authScheme}</span>}
                    </td>
                    <td style={{ padding: '8px 6px', color: outcomeColor(e.outcome), fontWeight: 600 }}>
                      {e.outcome} ({e.statusCode})
                    </td>
                  </tr>
                  {expanded === e.id && (
                    <tr>
                      <td colSpan={6} style={{ padding: 12, background: '#f8fafc', fontSize: 13 }}>
                        <div><strong>Pad:</strong> <code>{e.method} {e.path}</code></div>
                        <div><strong>Auth:</strong> {e.authScheme}
                          {e.apiConnectionId != null && <> (connection #{e.apiConnectionId})</>}
                        </div>
                        <div><strong>Duur:</strong> {e.durationMs} ms</div>
                        {e.clientIp && <div><strong>Vanaf IP:</strong> {e.clientIp}</div>}
                        {e.body && (
                          <div style={{ marginTop: 8 }}>
                            <strong>Verstuurde tekst:</strong>
                            <pre style={{ whiteSpace: 'pre-wrap', margin: '4px 0 0', background: 'white', padding: 8, border: '1px solid #e5e7eb' }}>
                              {e.body}
                            </pre>
                          </div>
                        )}
                        {e.responsePreview && (
                          <div style={{ marginTop: 8 }}>
                            <strong>Antwoord:</strong>
                            <pre style={{ whiteSpace: 'pre-wrap', margin: '4px 0 0', background: 'white', padding: 8, border: '1px solid #e5e7eb' }}>
                              {e.responsePreview}
                            </pre>
                          </div>
                        )}
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </table>
        )}

        {totalPages > 1 && (
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 16 }}>
            <button className="btn" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
              Vorige
            </button>
            <span style={{ fontSize: 13 }}>
              Pagina {page} van {totalPages}
            </span>
            <button className="btn" disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}>
              Volgende
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
