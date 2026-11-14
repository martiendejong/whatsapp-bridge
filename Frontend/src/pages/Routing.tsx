import { useState, useEffect, useCallback } from 'react';
import { routing, RoutingContact, monitor, MonitorSubject, reasonFrom } from '../api';

/**
 * The outbound policy, editable without a deploy: who may be messaged, about what, and at which
 * hours in their own timezone. Numbers that appear nowhere in this list are never auto-messaged.
 */

type Draft = Omit<RoutingContact, 'id' | 'updatedAtUtc' | 'openNow'>;

const EMPTY: Draft = {
  phone: '',
  name: '',
  alias: '',
  enabled: true,
  timeZoneId: 'Europe/Amsterdam',
  windowStartHour: 0,
  windowEndHour: 24,
  categories: '*',
  fallbackPhone: '',
};

// The categories the bridge actually sends today. Free text stays allowed — a new sender should
// not have to wait for a frontend release — but these are the ones worth suggesting.
const KNOWN_CATEGORIES = ['*', 'approval', 'deploy:valsuani', 'deploy', 'serverdown', 'reply', 'other'];

const HOURS = Array.from({ length: 25 }, (_, i) => i);

// Same clamp the backend applies in IsInsideWindow. The form cannot produce an out-of-range hour,
// but a row seeded from appsettings or written by an older build can, and 0/0 was the case that
// mattered: this table printed 'gesloten' for it while the backend clamped the end up to 1, read
// the window as 00:00-01:00 and delivered. A routing table that misreports who gets woken is
// worse than no table at all.
const clampWindow = (c: { windowStartHour: number; windowEndHour: number }) => ({
  start: Math.min(Math.max(c.windowStartHour, 0), 23),
  end: Math.min(Math.max(c.windowEndHour, 1), 24),
});

const describeWindow = (c: { windowStartHour: number; windowEndHour: number }) => {
  const { start, end } = clampWindow(c);
  if (start === 0 && end === 24) return 'altijd';
  if (start === end) return 'gesloten';
  const pad = (h: number) => String(h).padStart(2, '0');
  return `${pad(start)}:00-${pad(end)}:00`;
};

export default function Routing() {
  const [contacts, setContacts] = useState<RoutingContact[]>([]);
  const [timezones, setTimezones] = useState<string[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const [draft, setDraft] = useState<Draft | null>(null);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [saving, setSaving] = useState(false);

  const [previewTo, setPreviewTo] = useState('');
  const [previewCategory, setPreviewCategory] = useState('approval');
  const [preview, setPreview] = useState<{ outcome: string; recipient: string | null; reason: string } | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      setContacts((await routing.list()).data);
    } catch {
      setError('Kon de routing-contacten niet laden.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    load();
    routing.timezones().then((r) => setTimezones(r.data)).catch(() => undefined);
  }, [load]);

  const startEdit = (c: RoutingContact) => {
    setEditingId(c.id);
    setDraft({
      phone: c.phone,
      name: c.name,
      alias: c.alias ?? '',
      enabled: c.enabled,
      timeZoneId: c.timeZoneId,
      windowStartHour: c.windowStartHour,
      windowEndHour: c.windowEndHour,
      categories: c.categories,
      fallbackPhone: c.fallbackPhone ?? '',
    });
  };

  const save = async () => {
    if (!draft) return;
    setSaving(true);
    setError('');
    try {
      await routing.save(draft);
      setDraft(null);
      setEditingId(null);
      await load();
    } catch (e: any) {
      setError(e?.response?.data?.error ?? 'Opslaan mislukt.');
    } finally {
      setSaving(false);
    }
  };

  const remove = async (c: RoutingContact) => {
    setError('');
    try {
      await routing.remove(c.id);
      await load();
    } catch {
      setError('Verwijderen mislukt.');
    }
  };

  const runPreview = async () => {
    setError('');
    try {
      setPreview((await routing.preview(previewTo, previewCategory)).data);
    } catch {
      setError('Preview mislukt.');
    }
  };

  const field = (label: string, control: React.ReactNode) => (
    <div>
      <label style={{ display: 'block', fontSize: 13, marginBottom: 4 }}>{label}</label>
      {control}
    </div>
  );

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) =>
    setDraft((d) => (d ? { ...d, [key]: value } : d));

  return (
    <div className="container" style={{ paddingTop: 24, paddingBottom: 48 }}>
      <h2>Routing</h2>
      <p style={{ color: '#555', marginTop: 4 }}>
        Wie krijgt welk soort bericht, en op welke uren in zijn eigen tijdzone. Een nummer dat
        hier niet staat, wordt nooit uit zichzelf benaderd · antwoorden op iemand die zelf iets
        vraagt lopen buiten deze lijst om.
      </p>

      {error && <div className="alert alert-error">{error}</div>}

      {/* ── Contacten ──────────────────────────────────────────────────────────── */}
      <div className="card" style={{ marginTop: 16 }}>
        {loading ? (
          <p>Laden...</p>
        ) : contacts.length === 0 ? (
          <p style={{ color: '#777' }}>
            Nog geen contacten. Zolang deze lijst leeg is, geldt alleen de oude allow-list uit
            appsettings.
          </p>
        ) : (
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
            <thead>
              <tr style={{ textAlign: 'left', borderBottom: '2px solid #e5e7eb' }}>
                <th style={{ padding: '8px 6px' }}>Naam</th>
                <th style={{ padding: '8px 6px' }}>Nummer</th>
                <th style={{ padding: '8px 6px' }}>Categorieën</th>
                <th style={{ padding: '8px 6px' }}>Venster</th>
                <th style={{ padding: '8px 6px' }}>Terugval</th>
                <th style={{ padding: '8px 6px' }}>Nu</th>
                <th style={{ padding: '8px 6px' }}></th>
              </tr>
            </thead>
            <tbody>
              {contacts.map((c) => (
                <tr key={c.id} style={{ borderBottom: '1px solid #f1f5f9', opacity: c.enabled ? 1 : 0.55 }}>
                  <td style={{ padding: '8px 6px' }}>
                    <strong>{c.name}</strong>
                    {c.alias && <span style={{ color: '#777' }}> · {c.alias}</span>}
                  </td>
                  <td style={{ padding: '8px 6px' }}>{c.phone}</td>
                  <td style={{ padding: '8px 6px' }}>
                    <code>{c.categories}</code>
                  </td>
                  <td style={{ padding: '8px 6px', whiteSpace: 'nowrap' }}>
                    {describeWindow(c)}
                    <div style={{ fontSize: 12, color: '#777' }}>{c.timeZoneId}</div>
                  </td>
                  <td style={{ padding: '8px 6px' }}>{c.fallbackPhone || <span style={{ color: '#aaa' }}>—</span>}</td>
                  <td style={{ padding: '8px 6px', fontWeight: 600, color: c.enabled && c.openNow ? '#15803d' : '#b45309' }}>
                    {!c.enabled ? 'gedempt' : c.openNow ? 'open' : 'dicht'}
                  </td>
                  <td style={{ padding: '8px 6px', textAlign: 'right', whiteSpace: 'nowrap' }}>
                    <button className="btn" onClick={() => startEdit(c)}>Bewerk</button>{' '}
                    <button className="btn" onClick={() => remove(c)}>Verwijder</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {!draft && (
          <button className="btn" style={{ marginTop: 16 }} onClick={() => { setEditingId(null); setDraft(EMPTY); }}>
            Contact toevoegen
          </button>
        )}
      </div>

      {/* ── Bewerken ───────────────────────────────────────────────────────────── */}
      {draft && (
        <div className="card" style={{ marginTop: 16 }}>
          <h3 style={{ marginTop: 0 }}>{editingId ? 'Contact bewerken' : 'Nieuw contact'}</h3>
          <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
            {field('Nummer', (
              <input
                value={draft.phone}
                onChange={(e) => set('phone', e.target.value)}
                placeholder="31633984381"
                // Het nummer is de sleutel: bij bewerken wijzig je de rij, niet wie hij is.
                disabled={editingId != null}
                style={{ padding: 8, minWidth: 180 }}
              />
            ))}
            {field('Naam', (
              <input value={draft.name} onChange={(e) => set('name', e.target.value)} style={{ padding: 8, minWidth: 160 }} />
            ))}
            {field('Alias', (
              <input
                value={draft.alias ?? ''}
                onChange={(e) => set('alias', e.target.value)}
                placeholder="martien"
                style={{ padding: 8, minWidth: 140 }}
              />
            ))}
            {field('Tijdzone', (
              <select value={draft.timeZoneId} onChange={(e) => set('timeZoneId', e.target.value)} style={{ padding: 8, minWidth: 200 }}>
                {(timezones.includes(draft.timeZoneId) ? timezones : [draft.timeZoneId, ...timezones]).map((tz) => (
                  <option key={tz} value={tz}>{tz}</option>
                ))}
              </select>
            ))}
            {field('Vanaf', (
              <select value={draft.windowStartHour} onChange={(e) => set('windowStartHour', Number(e.target.value))} style={{ padding: 8 }}>
                {HOURS.slice(0, 24).map((h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
              </select>
            ))}
            {field('Tot', (
              <select value={draft.windowEndHour} onChange={(e) => set('windowEndHour', Number(e.target.value))} style={{ padding: 8 }}>
                {HOURS.slice(1).map((h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
              </select>
            ))}
            {field('Terugval-nummer', (
              <input
                value={draft.fallbackPhone ?? ''}
                onChange={(e) => set('fallbackPhone', e.target.value)}
                placeholder="31633984381"
                style={{ padding: 8, minWidth: 180 }}
              />
            ))}
          </div>

          <div style={{ marginTop: 16 }}>
            <label style={{ display: 'block', fontSize: 13, marginBottom: 4 }}>
              Categorieën (komma-gescheiden, <code>*</code> = alles)
            </label>
            <input
              value={draft.categories}
              onChange={(e) => set('categories', e.target.value)}
              style={{ padding: 8, width: '100%', maxWidth: 520 }}
            />
            <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginTop: 8 }}>
              {KNOWN_CATEGORIES.map((cat) => (
                <button
                  key={cat}
                  className="btn"
                  style={{ padding: '4px 8px', fontSize: 12 }}
                  onClick={() => set('categories', cat === '*' ? '*' : addCategory(draft.categories, cat))}
                >
                  {cat}
                </button>
              ))}
            </div>
          </div>

          <div style={{ marginTop: 16 }}>
            <label style={{ fontSize: 13 }}>
              <input type="checkbox" checked={draft.enabled} onChange={(e) => set('enabled', e.target.checked)} />{' '}
              Actief · uit betekent: berichten gaan naar het terugval-nummer, niet naar hem
            </label>
          </div>

          <div style={{ marginTop: 16, display: 'flex', gap: 8 }}>
            <button className="btn" disabled={saving || !draft.phone || !draft.name} onClick={save}>
              {saving ? 'Opslaan...' : 'Opslaan'}
            </button>
            <button className="btn" onClick={() => { setDraft(null); setEditingId(null); }}>Annuleren</button>
          </div>
        </div>
      )}

      {/* ── Preview ────────────────────────────────────────────────────────────── */}
      <div className="card" style={{ marginTop: 16 }}>
        <h3 style={{ marginTop: 0 }}>Wie krijgt dit nu?</h3>
        <p style={{ color: '#555', marginTop: 4 }}>
          Rekent de politiek door zonder iets te versturen.
        </p>
        <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap', alignItems: 'flex-end' }}>
          {field('Naar', (
            <input
              value={previewTo}
              onChange={(e) => setPreviewTo(e.target.value)}
              placeholder="nummer of alias"
              style={{ padding: 8, minWidth: 200 }}
            />
          ))}
          {field('Categorie', (
            <input
              value={previewCategory}
              onChange={(e) => setPreviewCategory(e.target.value)}
              list="routing-categories"
              style={{ padding: 8, minWidth: 200 }}
            />
          ))}
          <datalist id="routing-categories">
            {KNOWN_CATEGORIES.filter((c) => c !== '*').map((c) => <option key={c} value={c} />)}
          </datalist>
          <button className="btn" disabled={!previewTo} onClick={runPreview}>Bereken</button>
        </div>

        {preview && (
          <div
            style={{
              marginTop: 16,
              padding: 12,
              background: '#f8fafc',
              border: '1px solid #e5e7eb',
              fontSize: 14,
            }}
          >
            <div style={{ fontWeight: 600, color: preview.recipient ? '#15803d' : '#b45309' }}>
              {preview.outcome}
              {preview.recipient && <> · naar {preview.recipient}</>}
            </div>
            <div style={{ marginTop: 4, color: '#555' }}>{preview.reason}</div>
          </div>
        )}
      </div>

      <MonitorSection />
    </div>
  );
}

/**
 * De servermonitor: welke domeinen kritiek zijn (melding na 5 min offline) en welke normaal
 * (30 min), plus de actuele staat. Monitors melden zich elke ~5 minuten bij
 * POST /api/wa/monitor; de bridge beslist wat een appje waard is. Onbekende domeinen
 * registreren zichzelf als normaal — het niveau is hier aan te passen zonder deploy.
 */
function MonitorSection() {
  const [subjects, setSubjects] = useState<MonitorSubject[]>([]);
  const [error, setError] = useState('');
  const [newSubject, setNewSubject] = useState('');
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      setSubjects((await monitor.subjects()).data);
      setError('');
    } catch {
      setError('Kon de monitorlijst niet laden.');
    }
  }, []);

  useEffect(() => {
    load();
    const iv = setInterval(load, 30000);
    return () => clearInterval(iv);
  }, [load]);

  const setTier = async (s: MonitorSubject, critical: boolean) => {
    setBusy(true);
    try {
      await monitor.save(s.subject, critical);
      await load();
    } catch (e) {
      setError(reasonFrom(e) ?? 'Niveau wijzigen mislukt.');
    } finally {
      setBusy(false);
    }
  };

  const add = async () => {
    if (!newSubject.trim()) return;
    setBusy(true);
    try {
      await monitor.save(newSubject.trim(), true);
      setNewSubject('');
      await load();
    } catch (e) {
      setError(reasonFrom(e) ?? 'Toevoegen mislukt.');
    } finally {
      setBusy(false);
    }
  };

  const remove = async (s: MonitorSubject) => {
    setBusy(true);
    try {
      await monitor.remove(s.id);
      await load();
    } catch (e) {
      setError(reasonFrom(e) ?? 'Verwijderen mislukt.');
    } finally {
      setBusy(false);
    }
  };

  const stateLabel = (s: MonitorSubject) => {
    if (s.minutesSinceLastReport === null) return { text: 'nog nooit gemeld', color: '#777' };
    if (s.monitorSilent) return { text: `monitor stil (${s.minutesSinceLastReport} min)`, color: '#b45309' };
    if (s.status === 'down') return { text: s.downAlerted ? 'offline · gemeld' : 'offline · wacht op drempel', color: '#b91c1c' };
    return { text: 'online', color: '#15803d' };
  };

  return (
    <div className="card" style={{ marginTop: 16 }}>
      <h3 style={{ marginTop: 0 }}>Servermonitor</h3>
      <p style={{ color: '#555', marginTop: 4 }}>
        Kritiek = melding na 5 min offline, normaal = na 30 min. Onbekende servers melden zich
        vanzelf aan als normaal. Een monitor die zelf stilvalt geeft ook een melding.
      </p>
      {error && <div style={{ color: '#b91c1c', marginBottom: 8 }}>{error}</div>}

      {subjects.length > 0 && (
        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
          <thead>
            <tr style={{ textAlign: 'left', borderBottom: '2px solid #e5e7eb' }}>
              <th style={{ padding: '8px 6px' }}>Server</th>
              <th style={{ padding: '8px 6px' }}>Niveau</th>
              <th style={{ padding: '8px 6px' }}>Status</th>
              <th style={{ padding: '8px 6px' }}>Laatste melding</th>
              <th style={{ padding: '8px 6px' }}></th>
            </tr>
          </thead>
          <tbody>
            {subjects.map((s) => {
              const st = stateLabel(s);
              return (
                <tr key={s.id} style={{ borderBottom: '1px solid #f1f5f9' }}>
                  <td style={{ padding: '8px 6px' }}>
                    <strong>{s.subject}</strong>
                    {s.lastDetail && <div style={{ fontSize: 12, color: '#777' }}>{s.lastDetail}</div>}
                  </td>
                  <td style={{ padding: '8px 6px' }}>
                    <select
                      value={s.critical ? 'kritiek' : 'normaal'}
                      disabled={busy}
                      onChange={(e) => setTier(s, e.target.value === 'kritiek')}
                      style={{ padding: 4 }}
                    >
                      <option value="kritiek">kritiek · 5 min</option>
                      <option value="normaal">normaal · 30 min</option>
                    </select>
                  </td>
                  <td style={{ padding: '8px 6px', fontWeight: 600, color: st.color }}>{st.text}</td>
                  <td style={{ padding: '8px 6px', color: '#555' }}>
                    {s.minutesSinceLastReport === null ? '—' : `${s.minutesSinceLastReport} min geleden`}
                  </td>
                  <td style={{ padding: '8px 6px', textAlign: 'right' }}>
                    <button className="btn" disabled={busy} onClick={() => remove(s)}>Verwijder</button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}

      <div style={{ display: 'flex', gap: 8, marginTop: 12, alignItems: 'center' }}>
        <input
          value={newSubject}
          onChange={(e) => setNewSubject(e.target.value)}
          placeholder="domein, bv. nieuw.voorbeeld.nl"
          style={{ padding: 8, minWidth: 260 }}
        />
        <button className="btn" disabled={busy || !newSubject.trim()} onClick={add}>
          Toevoegen als kritiek
        </button>
      </div>
    </div>
  );
}

/** Voegt een categorie toe zonder de rest weg te gooien, en zonder dubbelingen. */
function addCategory(current: string, cat: string): string {
  const parts = current.split(',').map((p) => p.trim()).filter((p) => p && p !== '*');
  if (parts.includes(cat)) return parts.join(', ');
  return [...parts, cat].join(', ');
}
