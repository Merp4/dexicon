import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { JobsView } from './App';
import type { Corpus, Job, Progress } from './api';

/**
 * The jobs list, and the runs that did nothing.
 *
 * DEXICON__INDEXING__REFRESHMINUTES schedules one refresh per corpus per interval. On an
 * unchanged tree every one of them indexes nothing, so an hour of them is nineteen
 * identical "0 indexed, 30 skipped, 0 chunks" cards and the run that actually did
 * something is below the fold.
 */
const listJobs = vi.fn();

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  api: { listJobs: (...a: unknown[]) => listJobs(...a) },
}));

const corpora = [{ id: 'c1', name: 'docs' }, { id: 'c2', name: 'books' }] as Corpus[];

let seq = 0;

function job(over: Partial<Job> = {}): Job {
  seq += 1;
  return {
    id: `j${seq}`,
    corpusId: 'c1',
    kind: 'refresh',
    state: 'succeeded',
    filesDone: 0,
    filesSkipped: 30,
    filesFailed: 0,
    chunksWritten: 0,
    queuedUtc: new Date(Date.now() - 60_000 * seq).toISOString(),
    startedUtc: new Date(Date.now() - 60_000 * seq).toISOString(),
    finishedUtc: new Date(Date.now() - 60_000 * seq).toISOString(),
    error: null,
    ...over,
  } as unknown as Job;
}

/**
 * One `/api/events` frame, in the shape the server actually sends — captured from a live
 * run: `jobId` rather than `id`, a `phase`, and no `state` anywhere.
 */
function progress(over: Partial<Progress> = {}): Progress {
  return {
    jobId: 'j-live',
    corpusId: 'c1',
    phase: 'extract',
    filesTotal: 174,
    filesDone: 12,
    filesSkipped: 0,
    filesFailed: 0,
    chunksWritten: 40,
    currentFile: null,
    error: null,
    ...over,
  };
}

const props = { corpora, live: {}, onError: vi.fn() };

beforeEach(() => {
  seq = 0;
  vi.clearAllMocks();
});

describe('runs that found nothing to do', () => {
  it('collapses a stretch of them into one line', async () => {
    listJobs.mockResolvedValue([job(), job(), job({ corpusId: 'c2' })]);

    render(<JobsView {...props} />);

    expect(await screen.findByText(/3 scheduled refreshes found\s+nothing to do/)).toBeInTheDocument();
  });

  it('names the corpora it covered, each once', async () => {
    listJobs.mockResolvedValue([job(), job({ corpusId: 'c2' }), job({ corpusId: 'c2' })]);

    render(<JobsView {...props} />);

    expect(await screen.findByText('docs, books')).toBeInTheDocument();
  });

  it('leaves a run that did something as its own card', async () => {
    listJobs.mockResolvedValue([
      job(),
      job(),
      job({ filesDone: 8, chunksWritten: 51 }),
      job(),
      job(),
    ]);

    render(<JobsView {...props} />);

    expect(await screen.findByText('8 indexed')).toBeInTheDocument();
    // Two stretches, either side of it, rather than one covering the lot.
    expect(screen.getAllByText(/2 scheduled refreshes found\s+nothing to do/)).toHaveLength(2);
  });

  it('leaves a lone quiet run as a card, because one is not a stretch', async () => {
    // The summary of a single run is longer than the card it would replace, and it would
    // hide the counts that say which files were skipped.
    listJobs.mockResolvedValue([job({ filesDone: 8, chunksWritten: 51 }), job(), job({ filesDone: 4, chunksWritten: 9 })]);

    render(<JobsView {...props} />);

    expect(await screen.findByText('0 indexed')).toBeInTheDocument();
    expect(screen.queryByText(/scheduled refresh/)).not.toBeInTheDocument();
  });

  it('does not collapse a failure that wrote nothing', async () => {
    // Nothing indexed and nothing written is what a failed run looks like too. Folding it
    // away would hide the one job on the screen that needs reading.
    listJobs.mockResolvedValue([
      job(),
      job({ state: 'failed', error: 'Dexicon restarted while this job was running.' }),
      job(),
    ]);

    render(<JobsView {...props} />);

    expect(await screen.findByText(/Dexicon restarted/)).toBeInTheDocument();
    expect(screen.queryByText(/scheduled refresh/)).not.toBeInTheDocument();
  });
});

describe('what it asks the server for', () => {
  it('asks for activity only, so a page is days of real work rather than an hour of quiet', async () => {
    // The client can only collapse what it fetched. At one job per corpus per
    // REFRESHMINUTES, thirty rows is about an hour: measured on a live instance, 48 of
    // the last 200 jobs did real work and not one was in the most recent thirty.
    listJobs.mockResolvedValue([job({ filesDone: 8, chunksWritten: 51 })]);

    render(<JobsView {...props} />);

    await screen.findByText('8 indexed');
    expect(listJobs).toHaveBeenCalledWith(30, true);
  });

  it('asks for everything once routine refreshes are wanted', async () => {
    listJobs.mockResolvedValue([job()]);

    render(<JobsView {...props} />);
    await screen.findByText('0 indexed');

    await userEvent.click(screen.getByLabelText(/Show scheduled refreshes/));

    await vi.waitFor(() => expect(listJobs).toHaveBeenLastCalledWith(200, undefined));
  });

  it('ignores a reply from the mode it has already left', async () => {
    // Flipping the toggle leaves the previous request in flight. If it lands second it
    // paints the list nobody asked for: the routine runs appear, vanish when the
    // activity-only reply arrives behind them, and come back on the next poll.
    const routineRun = job();
    const realRun = job({ filesDone: 8, chunksWritten: 51 });

    let releaseSlowReply: (v: Job[]) => void = () => {};
    listJobs
      .mockImplementationOnce(() => new Promise<Job[]>((res) => { releaseSlowReply = res; }))
      .mockResolvedValue([routineRun]);

    render(<JobsView {...props} />);
    await userEvent.click(screen.getByLabelText(/Show scheduled refreshes/));
    await screen.findByText('0 indexed');

    // The activity-only reply from before the click, arriving late. Let it settle before
    // asserting: polling for "still absent" is satisfied by the first check, which is
    // before the reply has been applied, and passes whether or not it is ignored.
    await act(async () => {
      releaseSlowReply([realRun]);
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(screen.queryByText('8 indexed')).toBeNull();
    expect(screen.getByText('0 indexed')).toBeInTheDocument();
  });

  it('says a filter is on rather than claiming there is nothing', async () => {
    // "No jobs yet" under a hidden filter is the view lying about an empty database.
    listJobs.mockResolvedValue([]);

    render(<JobsView {...props} />);

    expect(await screen.findByText('Nothing has happened yet')).toBeInTheDocument();
  });
});

describe('a card carrying a live event', () => {
  it('takes its counters from the event, matched on jobId', async () => {
    // The event has no `id`, so matching one was false on every event and a running
    // job's counters never moved off whatever the last poll returned.
    listJobs.mockResolvedValue([job({ id: 'j-live', state: 'running', filesDone: 1 })]);

    render(<JobsView {...props} live={{ c1: progress({ jobId: 'j-live', filesDone: 12 }) }} />);

    expect(await screen.findByText('12 indexed')).toBeInTheDocument();
    expect(screen.queryByText('1 indexed')).toBeNull();
  });

  it('drops the progress bar once the run is over, and keeps the final counts', async () => {
    // The finished event stays in `live`, and its numbers are the right ones to show —
    // but a bar for a run that has ended is a page that looks stuck.
    listJobs.mockResolvedValue([job({ id: 'j-live', state: 'succeeded', filesDone: 1 })]);

    render(
      <JobsView
        {...props}
        live={{ c1: progress({ jobId: 'j-live', phase: 'Succeeded', filesDone: 174 }) }}
      />,
    );

    expect(await screen.findByText('174 indexed')).toBeInTheDocument();
    expect(screen.queryByRole('progressbar')).toBeNull();
  });
});

describe('how often it asks', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('polls slowly while nothing is running', async () => {
    // It polled every four seconds for as long as the tab was open, running or not.
    listJobs.mockResolvedValue([job({ filesDone: 8, chunksWritten: 51 })]);

    render(<JobsView {...props} />);
    await vi.waitFor(() => expect(listJobs).toHaveBeenCalledTimes(1));

    await vi.advanceTimersByTimeAsync(4_000);
    expect(listJobs).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(26_500);
    expect(listJobs).toHaveBeenCalledTimes(2);
  });

  it('polls fast while a run is live, without waiting for a slow tick to notice', async () => {
    // The progress stream fills `live` the moment a run starts, so the fast interval is
    // armed on that tick rather than up to thirty seconds later.
    //
    // A REAL event, captured off the wire: it carries `jobId` and a `phase` and it has
    // no `state` at all. The first version of this test invented a `state` field, so it
    // passed while the code it was guarding could never have fired in production.
    listJobs.mockResolvedValue([job({ filesDone: 8, chunksWritten: 51 })]);

    render(<JobsView {...props} live={{ c1: progress({ phase: 'extract' }) }} />);
    await vi.waitFor(() => expect(listJobs).toHaveBeenCalledTimes(1));

    await vi.advanceTimersByTimeAsync(4_500);
    expect(listJobs).toHaveBeenCalledTimes(2);
  });

  it('goes back to idle once the last event says the run finished', async () => {
    // The final event of a run carries the STATE NAME in `phase`, because the server
    // nulls Phase before it and sends `job.Phase ?? job.State.ToString()`. Waiting for
    // phase to go absent waits forever.
    listJobs.mockResolvedValue([job({ filesDone: 8, chunksWritten: 51 })]);

    render(<JobsView {...props} live={{ c1: progress({ phase: 'Succeeded' }) }} />);
    await vi.waitFor(() => expect(listJobs).toHaveBeenCalledTimes(1));

    await vi.advanceTimersByTimeAsync(4_500);
    expect(listJobs).toHaveBeenCalledTimes(1);
  });
});
