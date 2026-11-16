const OWNER = 'Safe-Passage-ASP-Org';
const REPO = 'safe_passage';
const GH_HEADERS = {
  'User-Agent': 'SafePassage-StatusHub',
  'Accept': 'application/vnd.github+json',
};

async function fetchJson(url, timeoutMs = 8000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const res = await fetch(url, { headers: GH_HEADERS, signal: controller.signal });
    if (!res.ok) return { ok: false, status: res.status, data: null };
    return { ok: true, status: res.status, data: await res.json() };
  } catch (err) {
    return { ok: false, status: 0, error: err.message, data: null };
  } finally {
    clearTimeout(timer);
  }
}

async function checkService(url, fallbackUrl = null) {
  const probe = async (target) => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 12000);
    const start = Date.now();
    try {
      const res = await fetch(target, { signal: controller.signal, redirect: 'follow' });
      const latencyMs = Date.now() - start;
      const contentType = res.headers.get('content-type') || '';
      let body = null;
      if (contentType.includes('application/json')) {
        body = await res.json().catch(() => null);
      }
      return { ok: res.ok, status: res.status, latencyMs, commit: body?.commit || null, system: body?.system || null };
    } catch (err) {
      return { ok: false, status: 0, latencyMs: Date.now() - start, error: err.message, commit: null };
    } finally {
      clearTimeout(timer);
    }
  };

  const primary = await probe(url);
  if (primary.ok || !fallbackUrl) return primary;
  return probe(fallbackUrl);
}

async function getRunDetails(run) {
  if (!run) return null;
  const jobsRes = await fetchJson(run.jobs_url);
  const jobs = (jobsRes.data?.jobs || []).map((j) => ({
    name: j.name,
    status: j.status,
    conclusion: j.conclusion,
    url: j.html_url,
    failedSteps: (j.steps || []).filter((s) => s.conclusion === 'failure').map((s) => s.name),
  }));

  return {
    id: run.id,
    status: run.status,
    conclusion: run.conclusion,
    headSha: run.head_sha,
    shortSha: (run.head_sha || '').slice(0, 7),
    url: run.html_url,
    updatedAt: run.updated_at,
    jobs,
    failedJobs: jobs.filter((j) => j.conclusion === 'failure'),
  };
}

async function getBranchState(branch) {
  const [commitRes, runsRes] = await Promise.all([
    fetchJson(`https://api.github.com/repos/${OWNER}/${REPO}/commits/${branch}`),
    fetchJson(`https://api.github.com/repos/${OWNER}/${REPO}/actions/runs?branch=${branch}&per_page=3`),
  ]);

  const commit = commitRes.data
    ? {
        sha: commitRes.data.sha,
        shortSha: commitRes.data.sha.slice(0, 7),
        message: (commitRes.data.commit?.message || '').split('\n')[0],
        author: commitRes.data.commit?.author?.name || commitRes.data.author?.login || 'unknown',
        date: commitRes.data.commit?.author?.date || null,
        url: commitRes.data.html_url,
      }
    : null;

  const runs = runsRes.data?.workflow_runs || [];
  const activeRaw = runs.find((r) => r.status === 'in_progress' || r.status === 'queued' || r.status === 'waiting') || null;
  const completedRaw = runs.find((r) => r.status === 'completed') || null;

  const [activeRun, latestCompletedRun] = await Promise.all([
    getRunDetails(activeRaw),
    getRunDetails(completedRaw),
  ]);

  return {
    branch,
    commit,
    activeRun,
    latestCompletedRun,
  };
}

export default async function handler(req, res) {
  const [devState, mainState, apiHealth, webHealth, mobileHealth] = await Promise.all([
    getBranchState('development'),
    getBranchState('main'),
    checkService('https://safe-passage-api.onrender.com/api/status'),
    checkService('https://safe-passage-web.onrender.com/health', 'https://safe-passage-web.onrender.com/'),
    checkService('https://safepassage-mobile.vercel.app/'),
  ]);

  const hasActiveRun = Boolean(devState.activeRun || mainState.activeRun);
  const ttl = hasActiveRun ? 15 : 30;
  res.setHeader('Cache-Control', `s-maxage=${ttl}, stale-while-revalidate=${ttl}`);

  res.status(200).json({
    checkedAt: new Date().toISOString(),
    hasActiveRun,
    branches: {
      development: devState,
      main: mainState,
    },
    services: [
      {
        id: 'api',
        name: 'Backend API (Render)',
        url: 'https://safe-passage-api.onrender.com/api/status',
        provider: 'Render Docker',
        ...apiHealth,
      },
      {
        id: 'web',
        name: 'Admin Web Portal (Render)',
        url: 'https://safe-passage-web.onrender.com/',
        provider: 'Render Docker',
        ...webHealth,
      },
      {
        id: 'mobile',
        name: 'Mobile Web App (Vercel)',
        url: 'https://safepassage-mobile.vercel.app/',
        provider: 'Vercel',
        ...mobileHealth,
      },
    ],
  });
}
