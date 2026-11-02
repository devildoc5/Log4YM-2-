import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus, Trash2, Save, Loader2, CheckCircle, AlertCircle, Wifi, WifiOff } from 'lucide-react';
import { api, LogbookDefinition, WsjtxSettings } from '../api/client';

type SaveState = { kind: 'idle' } | { kind: 'saving' } | { kind: 'ok'; text: string } | { kind: 'error'; text: string };

function SaveMessage({ state }: { state: SaveState }) {
  if (state.kind === 'ok') {
    return <span className="flex items-center gap-1 text-sm text-accent-success"><CheckCircle className="w-4 h-4" />{state.text}</span>;
  }
  if (state.kind === 'error') {
    return <span className="flex items-center gap-1 text-sm text-accent-danger"><AlertCircle className="w-4 h-4" />{state.text}</span>;
  }
  return null;
}

function errorText(error: unknown): string {
  const msg = error instanceof Error ? error.message : String(error);
  // "API error: 400 - There are two logbooks named …" → keep the server's sentence
  const dash = msg.indexOf(' - ');
  return dash >= 0 ? msg.slice(dash + 3) : msg;
}

// ---------------------------------------------------------------- Logbooks

/**
 * Logbooks: separate logs (e.g. a special-event station) inside the same database. Each can
 * have its own on-air callsign and TQSL station location. Removing a logbook here only removes
 * its settings — its QSOs stay and the logbook still shows while it has QSOs.
 */
export function LogbooksSettingsSection() {
  const queryClient = useQueryClient();
  const { data, isLoading } = useQuery({ queryKey: ['logbooks'], queryFn: () => api.getLogbooks() });

  const [rows, setRows] = useState<(LogbookDefinition & { qsoCount: number })[]>([]);
  const [active, setActive] = useState('Main');
  const [state, setState] = useState<SaveState>({ kind: 'idle' });

  useEffect(() => {
    if (!data) return;
    setActive(data.active);
    setRows(data.logbooks
      .filter(l => l.name !== 'Main')
      .map(l => ({
        name: l.name,
        stationCallsign: l.stationCallsign ?? '',
        tqslLocation: l.tqslLocation ?? '',
        description: l.description ?? '',
        qsoCount: l.qsoCount,
      })));
  }, [data]);

  const mainCount = data?.logbooks.find(l => l.name === 'Main')?.qsoCount ?? 0;

  const update = (i: number, patch: Partial<LogbookDefinition>) =>
    setRows(prev => prev.map((r, idx) => idx === i ? { ...r, ...patch } : r));

  const save = async () => {
    setState({ kind: 'saving' });
    try {
      const names = rows.map(r => r.name.trim()).filter(Boolean);
      await api.saveLogbooks({
        active: active === 'Main' || names.includes(active) ? active : 'Main',
        logbooks: rows
          .filter(r => r.name.trim())
          .map(({ name, stationCallsign, tqslLocation, description }) => ({ name: name.trim(), stationCallsign, tqslLocation, description })),
      });
      await queryClient.invalidateQueries({ queryKey: ['logbooks'] });
      setState({ kind: 'ok', text: 'Logbooks saved' });
    } catch (error) {
      setState({ kind: 'error', text: errorText(error) });
    }
  };

  if (isLoading) {
    return <div className="flex items-center gap-2 text-dark-300"><Loader2 className="w-4 h-4 animate-spin" /> Loading logbooks…</div>;
  }

  return (
    <div className="space-y-6">
      <div>
        <h3 className="text-lg font-semibold font-ui text-dark-200 mb-1">Logbooks</h3>
        <p className="text-sm text-dark-300">
          Keep special events in their own log. New QSOs go into the <strong>active</strong> logbook,
          tagged with its on-air callsign, and LOTW uploads sign them with its TQSL station location.
          Log History can filter by logbook, and the worked-before panel covers all logbooks.
        </p>
      </div>

      <div className="space-y-2">
        <label className="flex items-center gap-3 p-3 bg-dark-700/50 rounded-lg border border-glass-100 cursor-pointer">
          <input type="radio" name="active-logbook" checked={active === 'Main'} onChange={() => setActive('Main')} />
          <div className="flex-1">
            <p className="font-medium font-ui text-dark-200">Main</p>
            <p className="text-xs text-dark-300">Your everyday log, using the station callsign · {mainCount.toLocaleString()} QSOs</p>
          </div>
        </label>

        {rows.map((row, i) => (
          <div key={i} className="p-3 bg-dark-700/50 rounded-lg border border-glass-100 space-y-2">
            <div className="flex items-center gap-3">
              <input
                type="radio"
                name="active-logbook"
                checked={active === row.name && row.name !== ''}
                onChange={() => setActive(row.name)}
                title="Log new QSOs into this logbook"
              />
              <input
                type="text"
                value={row.name}
                onChange={(e) => {
                  const wasActive = active === row.name;
                  update(i, { name: e.target.value });
                  if (wasActive) setActive(e.target.value);
                }}
                placeholder="Logbook name, e.g. W7A Special Event 2026"
                className="glass-input flex-1"
              />
              <span className="text-xs text-dark-400 whitespace-nowrap">{row.qsoCount.toLocaleString()} QSOs</span>
              <button
                type="button"
                onClick={() => setRows(prev => prev.filter((_, idx) => idx !== i))}
                className="p-1.5 rounded text-dark-300 hover:text-accent-danger hover:bg-dark-600"
                title={row.qsoCount > 0 ? 'Remove these settings (the QSOs stay in your log)' : 'Remove logbook'}
              >
                <Trash2 className="w-4 h-4" />
              </button>
            </div>
            <div className="grid grid-cols-2 gap-2 pl-7">
              <input
                type="text"
                value={row.stationCallsign ?? ''}
                onChange={(e) => update(i, { stationCallsign: e.target.value.toUpperCase() })}
                placeholder="On-air callsign (blank = station call)"
                className="glass-input font-mono text-sm"
              />
              <input
                type="text"
                value={row.tqslLocation ?? ''}
                onChange={(e) => update(i, { tqslLocation: e.target.value })}
                placeholder="TQSL station location (blank = LOTW default)"
                className="glass-input text-sm"
              />
              <input
                type="text"
                value={row.description ?? ''}
                onChange={(e) => update(i, { description: e.target.value })}
                placeholder="Notes (optional)"
                className="glass-input text-sm col-span-2"
              />
            </div>
          </div>
        ))}

        <button
          type="button"
          onClick={() => setRows(prev => [...prev, { name: '', stationCallsign: '', tqslLocation: '', description: '', qsoCount: 0 }])}
          className="glass-button px-3 py-1.5 flex items-center gap-2 text-sm"
        >
          <Plus className="w-4 h-4" /> Add logbook
        </button>
      </div>

      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={save}
          disabled={state.kind === 'saving'}
          className="glass-button px-4 py-2 flex items-center gap-2 text-accent-success disabled:opacity-50"
        >
          {state.kind === 'saving' ? <Loader2 className="w-4 h-4 animate-spin" /> : <Save className="w-4 h-4" />}
          Save logbooks
        </button>
        <SaveMessage state={state} />
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- WSJT-X

/**
 * WSJT-X / JTDX / MSHV auto-logging. Log4YM listens for the "logged ADIF" UDP message those
 * programs send when you log a QSO, and saves it into the active logbook.
 */
export function WsjtxSettingsSection() {
  const { data: status, refetch } = useQuery({
    queryKey: ['wsjtx-status'],
    queryFn: () => api.getWsjtxStatus(),
    refetchInterval: 3000,
  });

  const [form, setForm] = useState<WsjtxSettings>({ enabled: false, address: '0.0.0.0', port: 2237, forwardTo: [] });
  const [forwardText, setForwardText] = useState('');
  const [loaded, setLoaded] = useState(false);
  const [state, setState] = useState<SaveState>({ kind: 'idle' });

  // Fill the form once from the server; after that, the operator's edits win over polling
  useEffect(() => {
    if (!status || loaded) return;
    setForm({ enabled: status.enabled, address: status.address, port: status.port, forwardTo: status.forwardTo });
    setForwardText(status.forwardTo.join(', '));
    setLoaded(true);
  }, [status, loaded]);

  const save = async () => {
    setState({ kind: 'saving' });
    try {
      const forwardTo = forwardText.split(/[\s,]+/).map(s => s.trim()).filter(Boolean);
      const result = await api.saveWsjtxSettings({ ...form, forwardTo });
      setForm({ enabled: result.enabled, address: result.address, port: result.port, forwardTo: result.forwardTo });
      await refetch();
      setState(result.lastError
        ? { kind: 'error', text: result.lastError }
        : { kind: 'ok', text: result.enabled ? (result.listening ? 'Saved — listening' : 'Saved') : 'Saved — auto-logging off' });
    } catch (error) {
      setState({ kind: 'error', text: errorText(error) });
    }
  };

  const isMulticast = /^2(2[4-9]|3\d)\./.test(form.address.trim());

  return (
    <div className="space-y-6">
      <div>
        <h3 className="text-lg font-semibold font-ui text-dark-200 mb-1">WSJT-X auto-logging</h3>
        <p className="text-sm text-dark-300">
          When you log a QSO in WSJT-X, JTDX or MSHV, it's saved here automatically — into the active
          logbook, with duplicates skipped. Uploading to QRZ and LOTW stays manual.
        </p>
      </div>

      <div className="flex items-center justify-between p-4 bg-dark-700/50 rounded-lg border border-glass-100">
        <div>
          <p className="font-medium font-ui text-dark-200">Log QSOs from WSJT-X</p>
          <p className="text-sm text-dark-300">Listen for "QSO logged" messages over UDP</p>
        </div>
        <button
          type="button"
          onClick={() => setForm(f => ({ ...f, enabled: !f.enabled }))}
          className={`relative w-12 h-7 rounded-full transition-colors ${form.enabled ? 'bg-accent-success' : 'bg-dark-600 border border-dark-400'}`}
          aria-pressed={form.enabled}
          aria-label="Log QSOs from WSJT-X"
        >
          <span className={`absolute top-1 left-1 w-5 h-5 rounded-full bg-white transition-transform ${form.enabled ? 'translate-x-5' : 'translate-x-0'}`} />
        </button>
      </div>

      <div className={`space-y-4 ${!form.enabled ? 'opacity-50' : ''}`}>
        <div className="grid grid-cols-3 gap-3">
          <div className="col-span-2 space-y-1">
            <label className="text-sm font-ui text-dark-300">Listen address</label>
            <input
              type="text"
              value={form.address}
              onChange={(e) => setForm(f => ({ ...f, address: e.target.value }))}
              className="glass-input w-full font-mono"
              placeholder="0.0.0.0"
            />
          </div>
          <div className="space-y-1">
            <label className="text-sm font-ui text-dark-300">Port</label>
            <input
              type="number"
              value={form.port}
              onChange={(e) => setForm(f => ({ ...f, port: parseInt(e.target.value, 10) || 0 }))}
              className="glass-input w-full font-mono"
              min={1}
              max={65535}
            />
          </div>
        </div>
        <p className="text-xs text-dark-300">
          <span className="font-mono">0.0.0.0</span> listens on every network card, so a WSJT-X on another PC can
          send to this one. To share the stream with GridTracker or JTAlert, use a multicast address such as{' '}
          <span className="font-mono">224.0.0.73</span> here and in every program.
          {isMulticast && <span className="text-accent-success"> Multicast — other apps can listen too.</span>}
        </p>

        <div className="space-y-1">
          <label className="text-sm font-ui text-dark-300">Forward to (optional)</label>
          <input
            type="text"
            value={forwardText}
            onChange={(e) => setForwardText(e.target.value)}
            className="glass-input w-full font-mono"
            placeholder="127.0.0.1:2238, 192.168.1.20:2237"
          />
          <p className="text-xs text-dark-300">
            Re-sends everything WSJT-X sends to these host:port addresses — for apps that need the
            stream when you're not using multicast.
          </p>
        </div>

        <div className="p-3 bg-dark-700/50 rounded-lg border border-glass-100 text-sm text-dark-300 space-y-1">
          <p className="font-medium text-dark-200">In WSJT-X: File → Settings → Reporting</p>
          <p>UDP Server: <span className="font-mono">{isMulticast ? form.address : '127.0.0.1'}</span> (or this PC's address if WSJT-X is on another computer)
            · UDP Server port: <span className="font-mono">{form.port}</span></p>
          <p>Turn on "Prompt me to log QSO" or "Log automatically", then click Log QSO as usual.</p>
        </div>
      </div>

      {status && (
        <div className="flex items-start gap-3 p-3 rounded-lg border border-glass-100 bg-dark-700/30 text-sm">
          {status.listening ? <Wifi className="w-4 h-4 text-accent-success mt-0.5" /> : <WifiOff className="w-4 h-4 text-dark-400 mt-0.5" />}
          <div className="space-y-0.5">
            <p className="text-dark-200">
              {status.listening ? `Listening on ${status.address}:${status.port}` : status.enabled ? 'Not listening' : 'Off'}
            </p>
            {(status.qsosLogged > 0 || status.duplicatesSkipped > 0) && (
              <p className="text-dark-300">
                {status.qsosLogged} logged this session
                {status.duplicatesSkipped > 0 && `, ${status.duplicatesSkipped} duplicate(s) skipped`}
                {status.lastCallsign && <> · last: <span className="font-mono">{status.lastCallsign}</span>{status.lastClientId && ` from ${status.lastClientId}`}</>}
              </p>
            )}
            {status.lastError && <p className="text-accent-danger">{status.lastError}</p>}
          </div>
        </div>
      )}

      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={save}
          disabled={state.kind === 'saving'}
          className="glass-button px-4 py-2 flex items-center gap-2 text-accent-success disabled:opacity-50"
        >
          {state.kind === 'saving' ? <Loader2 className="w-4 h-4 animate-spin" /> : <Save className="w-4 h-4" />}
          Save & restart listener
        </button>
        <SaveMessage state={state} />
      </div>
    </div>
  );
}
