import { useEffect, useState } from 'react';
import { History, Compass } from 'lucide-react';
import { api, WorkedBeforeResponse, WorkedStatus } from '../api/client';
import { gridToLatLon, calculateDistance, calculateBearing } from '../utils/maidenhead';

const BAND_ORDER = ['160m', '80m', '60m', '40m', '30m', '20m', '17m', '15m', '12m', '10m', '6m', '2m', '70cm'];

interface Props {
  callsign: string;
  band: string;
  mode: string;
  /** Grid typed in the form or from the callsign lookup */
  grid?: string | null;
  /** Their position from the callsign lookup, used for distance when there's no grid */
  latitude?: number | null;
  longitude?: number | null;
  /** My station's position */
  myGrid?: string | null;
  myLatitude?: number | null;
  myLongitude?: number | null;
  /** Check only this logbook; omit for all logbooks */
  logbook?: string;
}

type Level = 'new' | 'worked' | 'confirmed';

function levelOn(status: WorkedStatus | null | undefined, scope: 'any' | 'band' | 'mode' | 'bandMode'): Level {
  if (!status) return 'new';
  const confirmed = scope === 'any' ? status.confirmed
    : scope === 'band' ? status.confirmedOnBand
    : scope === 'mode' ? status.confirmedOnMode
    : status.confirmedOnBandMode;
  const worked = scope === 'any' ? status.worked
    : scope === 'band' ? status.workedOnBand
    : scope === 'mode' ? status.workedOnMode
    : status.workedOnBandMode;
  return confirmed ? 'confirmed' : worked ? 'worked' : 'new';
}

const LEVEL_STYLE: Record<Level, string> = {
  new: 'bg-accent-danger/20 text-accent-danger border-accent-danger/40',
  worked: 'bg-accent-warning/20 text-accent-warning border-accent-warning/40',
  confirmed: 'bg-accent-success/20 text-accent-success border-accent-success/40',
};

const LEVEL_LABEL: Record<Level, string> = { new: 'NEW', worked: 'Wkd', confirmed: 'Cfm' };

function Cell({ level, title }: { level: Level; title: string }) {
  return (
    <span
      className={`inline-flex justify-center min-w-[2.5rem] px-1 py-0.5 rounded border text-[10px] font-mono font-bold ${LEVEL_STYLE[level]}`}
      title={title}
    >
      {LEVEL_LABEL[level]}
    </span>
  );
}

function Row({ label, keyText, status, band, mode }: {
  label: string;
  keyText?: string | null;
  status: WorkedStatus | null | undefined;
  band: string;
  mode: string;
}) {
  return (
    <tr>
      <td className="pr-2 text-dark-300 whitespace-nowrap">
        {label}
        {keyText && <span className="ml-1 font-mono text-gray-200">{keyText}</span>}
      </td>
      <td className="px-0.5"><Cell level={levelOn(status, 'any')} title={`${label}: any band, any mode`} /></td>
      <td className="px-0.5"><Cell level={levelOn(status, 'band')} title={`${label} on ${band}`} /></td>
      <td className="px-0.5"><Cell level={levelOn(status, 'mode')} title={`${label} on ${mode}`} /></td>
      <td className="px-0.5"><Cell level={levelOn(status, 'bandMode')} title={`${label} on ${band} ${mode}`} /></td>
    </tr>
  );
}

/** Format km as "1,234 mi / 1,986 km". */
function formatDistance(km: number): string {
  const mi = km * 0.621371;
  return `${Math.round(mi).toLocaleString()} mi / ${Math.round(km).toLocaleString()} km`;
}

/**
 * Worked-before panel for the log entry form: has this callsign, its grid and its
 * DXCC entity been worked or confirmed — overall, on this band, on this mode, and on both.
 */
export function WorkedBeforeCard(props: Props) {
  const { callsign, band, mode, grid, logbook } = props;
  const [data, setData] = useState<WorkedBeforeResponse | null>(null);
  const [error, setError] = useState(false);

  useEffect(() => {
    const call = callsign.trim();
    if (call.length < 3) {
      setData(null);
      return;
    }

    // Debounce typing, and drop answers for callsigns the operator has moved past
    const controller = new AbortController();
    const timer = setTimeout(() => {
      api.getWorkedBefore({ callsign: call, band, mode, grid: grid || undefined, logbook }, controller.signal)
        .then(result => { setData(result); setError(false); })
        .catch(err => { if (err?.name !== 'AbortError') setError(true); });
    }, 250);

    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [callsign, band, mode, grid, logbook]);

  if (callsign.trim().length < 3) return null;
  if (error) return null;
  if (!data || data.callsign !== callsign.trim().toUpperCase()) return null;

  // Distance and bearing from my station
  const mine = props.myLatitude != null && props.myLongitude != null
    ? { lat: props.myLatitude, lon: props.myLongitude }
    : props.myGrid ? gridToLatLon(props.myGrid) : null;
  const theirGrid = grid || data.grid?.key || null;
  const theirs = theirGrid ? gridToLatLon(theirGrid)
    : props.latitude != null && props.longitude != null ? { lat: props.latitude, lon: props.longitude }
    : null;
  const distanceKm = mine && theirs ? calculateDistance(mine.lat, mine.lon, theirs.lat, theirs.lon) : null;
  const bearing = mine && theirs ? calculateBearing(mine.lat, mine.lon, theirs.lat, theirs.lon) : null;

  const worked = new Set(data.entity?.bandsWorked ?? []);
  const confirmed = new Set(data.entity?.bandsConfirmed ?? []);
  const bands = BAND_ORDER.filter(b => worked.has(b) || b === band);

  return (
    <div className="bg-dark-700/50 rounded-lg p-2 border border-glass-100 animate-fade-in space-y-2" data-testid="worked-before">
      <div className="flex items-center justify-between text-xs font-ui">
        <span className="flex items-center gap-1 text-dark-300">
          <History className="w-3 h-3" />
          {data.call.worked
            ? <>Worked <span className="text-gray-100 font-bold">{data.call.qsoCount}×</span> before</>
            : <span className="text-accent-danger font-bold">First QSO with {data.callsign}</span>}
          {logbook && <span className="ml-1 text-dark-400">in {logbook}</span>}
        </span>
        {distanceKm != null && (
          <span className="flex items-center gap-1 text-dark-300" title="Distance and beam heading from your station">
            <Compass className="w-3 h-3" />
            <span className="font-mono text-gray-200">{formatDistance(distanceKm)}</span>
            {bearing != null && <span className="font-mono">{Math.round(bearing)}°</span>}
          </span>
        )}
      </div>

      <table className="text-xs font-ui w-full">
        <thead>
          <tr className="text-[10px] text-dark-400">
            <th className="text-left font-normal"></th>
            <th className="font-normal">Any</th>
            <th className="font-normal font-mono">{band}</th>
            <th className="font-normal font-mono">{mode}</th>
            <th className="font-normal font-mono">{band} {mode}</th>
          </tr>
        </thead>
        <tbody>
          <Row label="Call" status={data.call} band={band} mode={mode} />
          <Row label="Grid" keyText={data.grid?.key} status={data.grid} band={band} mode={mode} />
          <Row label="DXCC" keyText={data.entity?.key ? titleCase(data.entity.key) : null} status={data.entity} band={band} mode={mode} />
        </tbody>
      </table>

      {data.entity && bands.length > 0 && (
        <div className="flex flex-wrap gap-1" title="Bands you've worked this DXCC entity on (green = confirmed)">
          {bands.map(b => (
            <span
              key={b}
              className={`px-1 rounded text-[10px] font-mono border ${
                confirmed.has(b) ? LEVEL_STYLE.confirmed : worked.has(b) ? LEVEL_STYLE.worked : LEVEL_STYLE.new
              } ${b === band ? 'ring-1 ring-accent-primary' : ''}`}
            >
              {b}
            </span>
          ))}
        </div>
      )}

      {data.lastQso && (
        <div className="text-[10px] text-dark-400 font-ui">
          Last: <span className="font-mono text-dark-300">
            {data.lastQso.qsoDate.slice(0, 10)} {data.lastQso.band} {data.lastQso.mode}
          </span>
          {data.lastQso.logbook && data.lastQso.logbook !== 'Main' && <> · {data.lastQso.logbook}</>}
        </div>
      )}
    </div>
  );
}

function titleCase(s: string): string {
  return s.toLowerCase().replace(/\b([a-z])/g, c => c.toUpperCase());
}
