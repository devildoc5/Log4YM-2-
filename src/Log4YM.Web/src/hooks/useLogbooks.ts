import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';

/** Logbooks with QSO counts and which one is active. Shared by the entry panel, Log History and Settings. */
export function useLogbooks() {
  return useQuery({
    queryKey: ['logbooks'],
    queryFn: () => api.getLogbooks(),
    staleTime: 30_000,
  });
}
