import { useMutation, useQueryClient } from '@tanstack/react-query';
import { BookOpen } from 'lucide-react';
import { api } from '../api/client';
import { useLogbooks } from '../hooks/useLogbooks';

const NEW_OPTION = '__new__';

/**
 * Compact switcher for the logbook new QSOs go into (e.g. "Main" or a special-event log).
 * Picking "New logbook…" asks for a name and creates it.
 */
export function LogbookSelector({ className = '' }: { className?: string }) {
  const queryClient = useQueryClient();
  const { data } = useLogbooks();

  const setActive = useMutation({
    mutationFn: (name: string) => api.setActiveLogbook(name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['logbooks'] });
      queryClient.invalidateQueries({ queryKey: ['qsos'] });
    },
  });

  const active = data?.active ?? 'Main';
  const logbooks = data?.logbooks ?? [{ name: 'Main', qsoCount: 0, isActive: true, isDefined: true }];
  const activeInfo = logbooks.find(l => l.name === active);

  const onChange = (value: string) => {
    if (value === NEW_OPTION) {
      const name = window.prompt('Name for the new logbook (e.g. "W7A Special Event 2026"):')?.trim();
      if (name) setActive.mutate(name);
      return;
    }
    setActive.mutate(value);
  };

  return (
    <label
      className={`flex items-center gap-1 text-xs font-ui ${active !== 'Main' ? 'text-accent-warning' : 'text-dark-300'} ${className}`}
      title={activeInfo?.stationCallsign
        ? `New QSOs go into "${active}" as ${activeInfo.stationCallsign}`
        : `New QSOs go into "${active}"`}
    >
      <BookOpen className="w-3.5 h-3.5" />
      <select
        value={active}
        onChange={(e) => onChange(e.target.value)}
        disabled={setActive.isPending}
        className="glass-input !py-0.5 !px-1 text-xs max-w-[11rem]"
        aria-label="Active logbook"
      >
        {logbooks.map(l => (
          <option key={l.name} value={l.name}>
            {l.name}{l.stationCallsign ? ` (${l.stationCallsign})` : ''}
          </option>
        ))}
        <option value={NEW_OPTION}>New logbook…</option>
      </select>
    </label>
  );
}
