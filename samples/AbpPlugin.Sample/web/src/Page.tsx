import { useEffect, useState } from 'react';

type Ping = { key: string; version: string; visits: number; last?: string | null };

const api = '/api/plugins/acme.abp-sample';

/** The sample plugin's page: what its endpoint says, and a visit recorded with a note. */
export default function Page() {
  const [ping, setPing] = useState<Ping | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState('');

  const load = async () => {
    const response = await fetch(`${api}/ping`, { credentials: 'same-origin' });
    if (!response.ok) throw new Error(`ping answered ${response.status}`);
    setPing(await response.json());
  };

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

  const visit = async () => {
    const response = await fetch(`${api}/visit?note=${encodeURIComponent(note || 'from the page')}`, { method: 'POST', credentials: 'same-origin' });
    if (!response.ok) return setError(`visit answered ${response.status}`);
    setNote('');
    await load();
  };

  if (error) return <p role="alert">The sample plugin couldn't be reached: {error}</p>;
  if (!ping) return <p>Loading…</p>;
  return (
    <section data-plugin="acme.abp-sample">
      <h2>AbpPlugin sample {ping.version}</h2>
      <p>
        {ping.visits} {ping.visits === 1 ? 'visit' : 'visits'}
        {ping.last ? `, the last: “${ping.last}”` : ''}
      </p>
      <label>
        Note <input value={note} onChange={(e) => setNote(e.target.value)} />
      </label>{' '}
      <button type="button" onClick={visit}>
        Record a visit
      </button>
    </section>
  );
}
