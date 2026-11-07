import { useCallback, useEffect, useRef, useState } from 'react';
import type { Entry, QueueResponse } from './model';

async function request(path: string, body?: object, signal?: AbortSignal): Promise<QueueResponse> {
  const response = await fetch(
    `/api/triage${path}`,
    body === undefined
      ? { signal }
      : {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body),
          signal,
        },
  );
  const result: QueueResponse = await response.json();
  if (!response.ok) throw new Error(result.error || `Request failed (${response.status})`);
  return result;
}

export function useQueue() {
  const [entries, setEntries] = useState<Entry[]>([]);
  const [loading, setLoading] = useState(true);
  const [pending, setPending] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [checked, setChecked] = useState('');
  const [checkedScope, setCheckedScope] = useState('selected');
  const busy = useRef(false);
  const mounted = useRef(true);
  useEffect(() => {
    const controller = new AbortController();
    mounted.current = true;
    request('', undefined, controller.signal)
      .then((data) => setEntries(data.entries))
      .catch((error: Error) => {
        if (error.name !== 'AbortError') setError(error.message);
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => {
      mounted.current = false;
      controller.abort();
    };
  }, []);

  const perform = useCallback(async (path: string, body: object, message = ''): Promise<boolean> => {
    if (busy.current) return false;
    busy.current = true;
    setPending(path);
    setError('');
    setNotice('');
    try {
      const data = await request(path, body);
      if (mounted.current) {
        setEntries(data.entries);
        if (path !== '/interact') {
          setChecked(new Date().toISOString());
          setCheckedScope(path === '/refresh' ? 'estate' : 'selected');
        }
        setNotice(message);
      }
      return true;
    } catch (error) {
      if (mounted.current)
        setError(error instanceof Error ? error.message : 'The operation failed. Try again.');
      try {
        const saved = await request('');
        if (mounted.current) setEntries(saved.entries);
      } catch {
        /* Keep last known data. */
      }
      return false;
    } finally {
      busy.current = false;
      if (mounted.current) setPending('');
    }
  }, []);
  return { entries, loading, pending, error, notice, checked, checkedScope, perform };
}
export type QueueState = ReturnType<typeof useQueue>;
