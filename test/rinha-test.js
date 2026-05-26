import http from 'k6/http';
import { SharedArray } from 'k6/data';
import { Counter } from 'k6/metrics';
import exec from 'k6/execution';

const baseUrl = __ENV.BASE_URL || 'http://localhost:9999';
const targetRate = Number(__ENV.TARGET_RATE || '900');
const rampDuration = __ENV.RAMP_DURATION || '120s';
const startRate = Number(__ENV.START_RATE || '1');
const preAllocatedVUs = Number(__ENV.PRE_ALLOCATED_VUS || '100');
const maxVUs = Number(__ENV.MAX_VUS || '250');
const requestTimeout = __ENV.REQUEST_TIMEOUT || '2001ms';
const resultsPath = __ENV.RESULTS_PATH || 'test/results.json';
const dumpMismatches = __ENV.DUMP_MISMATCHES === '1';

const testData = new SharedArray('test-data', function () {
  return JSON.parse(open('./test-data.json')).entries;
});

const statsArr = new SharedArray('test-stats', function () {
  return [JSON.parse(open('./test-data.json')).stats];
});

const expectedStats = statsArr[0];
const tpCount = new Counter('tp_count');
const tnCount = new Counter('tn_count');
const fpCount = new Counter('fp_count');
const fnCount = new Counter('fn_count');
const errorCount = new Counter('error_count');

export const options = {
  summaryTrendStats: ['p(99)'],
  systemTags: ['status', 'method'],
  dns: {
    ttl: '5m',
    select: 'roundRobin',
  },
  scenarios: {
    default: {
      executor: 'ramping-arrival-rate',
      startRate,
      timeUnit: '1s',
      preAllocatedVUs,
      maxVUs,
      gracefulStop: '10s',
      stages: [
        { duration: rampDuration, target: targetRate },
      ],
    },
  },
};

export function setup() {
  console.log(
    `Dataset: ${expectedStats.total} entries, ` +
    `${expectedStats.fraud_count} fraud (${expectedStats.fraud_rate}%), ` +
    `${expectedStats.legit_count} legit (${expectedStats.legit_rate}%), ` +
    `edge cases: ${expectedStats.edge_case_rate}%`
  );
}

export default function () {
  const idx = exec.scenario.iterationInTest;
  if (idx >= testData.length) return;

  const entry = testData[idx];
  const expectedApproved = entry.expected_approved;
  const res = http.post(
    `${baseUrl}/fraud-score`,
    JSON.stringify(entry.request),
    { headers: { 'Content-Type': 'application/json' }, timeout: requestTimeout }
  );

  if (res.status === 200) {
    const body = JSON.parse(res.body);
    if (expectedApproved === body.approved) {
      if (body.approved) tnCount.add(1);
      else tpCount.add(1);
    } else {
      if (dumpMismatches) {
        console.log(JSON.stringify({
          idx,
          expected_approved: expectedApproved,
          approved: body.approved,
          fraud_score: body.fraud_score,
          request: entry.request,
        }));
      }
      if (body.approved) fnCount.add(1);
      else fpCount.add(1);
    }
  } else {
    errorCount.add(1);
  }
}

export function handleSummary(data) {
  const K = 1000;
  const T_MAX_MS = 1000;
  const P99_MIN_MS = 1;
  const P99_MAX_MS = 2000;
  const EPSILON_MIN = 0.001;
  const BETA = 300;
  const TX_CORTE = 0.15;
  const SCORE_P99_CORTE = -3000;
  const SCORE_DET_CORTE = -3000;

  const httpDuration = data.metrics.http_req_duration.values;
  const p99 = httpDuration['p(99)'];
  const tp = data.metrics.tp_count ? data.metrics.tp_count.values.count : 0;
  const tn = data.metrics.tn_count ? data.metrics.tn_count.values.count : 0;
  const fp = data.metrics.fp_count ? data.metrics.fp_count.values.count : 0;
  const fn = data.metrics.fn_count ? data.metrics.fn_count.values.count : 0;
  const errs = data.metrics.error_count ? data.metrics.error_count.values.count : 0;
  const N = tp + tn + fp + fn + errs;
  const E = (fp * 1) + (fn * 3) + (errs * 5);
  const failures = fp + fn + errs;
  const epsilon = N > 0 ? E / N : 0;
  const failureRate = N > 0 ? failures / N : 0;

  let p99Score;
  let p99CutTriggered = false;
  if (p99 <= 0) {
    p99Score = 0;
  } else if (p99 > P99_MAX_MS) {
    p99Score = SCORE_P99_CORTE;
    p99CutTriggered = true;
  } else {
    p99Score = K * Math.log10(T_MAX_MS / Math.max(p99, P99_MIN_MS));
  }

  let detScore;
  let rateComponent = 0;
  let absolutePenalty = 0;
  let cutTriggered = false;
  if (failureRate > TX_CORTE) {
    detScore = SCORE_DET_CORTE;
    cutTriggered = true;
  } else {
    rateComponent = K * Math.log10(1 / Math.max(epsilon, EPSILON_MIN));
    absolutePenalty = -BETA * Math.log10(1 + E);
    detScore = rateComponent + absolutePenalty;
  }

  const finalScore = p99Score + detScore;
  const result = {
    expected: expectedStats,
    p99: p99.toFixed(2) + 'ms',
    scoring: {
      breakdown: {
        false_positive_detections: fp,
        false_negative_detections: fn,
        true_positive_detections: tp,
        true_negative_detections: tn,
        http_errors: errs,
      },
      failure_rate: +(failureRate * 100).toFixed(2) + '%',
      weighted_errors_E: E,
      error_rate_epsilon: +epsilon.toFixed(6),
      p99_score: {
        value: +p99Score.toFixed(2),
        cut_triggered: p99CutTriggered,
      },
      detection_score: {
        value: +detScore.toFixed(2),
        rate_component: cutTriggered ? null : +rateComponent.toFixed(2),
        absolute_penalty: cutTriggered ? null : +absolutePenalty.toFixed(2),
        cut_triggered: cutTriggered,
      },
      final_score: +finalScore.toFixed(2),
    },
  };

  return {
    [resultsPath]: JSON.stringify(result, null, 2),
  };
}
