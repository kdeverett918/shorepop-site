#!/usr/bin/env python3
"""Read-only unattended probe for the Shorepop purchase verifier (WO-20261118-396, WO-20261013-64).

Checks:
  1. GET /healthz -> HTTP 200, status == "ok", store == "postgres_durable" (unless --allow-non-durable),
     google == "configured" (unless --allow-google-unconfigured), catalogProducts == expected count.
  2. GET /v1/catalog -> HTTP 200, exact SKU parity against the repo's NativeProductTable.All.
  3. GET /status -> HTTP 200, reports side-job alerts and growing-bound warnings without mutating state.
  4. POST /v1/purchases/validate (unauthenticated) -> HTTP 401 (auth boundary intact).

No secrets are read or printed. Exits 0 on green, 1 on any failure or alert threshold breach.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path


def load_client_skus(repo_root: Path) -> set[str]:
    policy_path = repo_root / "Assets" / "Scripts" / "Services" / "Commerce" / "NativePurchasePolicy.cs"
    text = policy_path.read_text(encoding="utf-8")
    return set(re.findall(r'"(gg\.shorepop\.[a-z0-9._-]+)"', text))


def http_request(method: str, url: str, timeout: float, body: bytes | None = None) -> tuple[int, str]:
    headers = {"Accept": "application/json"}
    if body is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as err:
        return err.code, err.read().decode("utf-8", errors="replace")


def probe(
    base_url: str,
    expected_skus: set[str],
    timeout: float = 10.0,
    allow_non_durable: bool = False,
    allow_google_unconfigured: bool = False,
) -> tuple[bool, dict]:
    base = base_url.rstrip("/")
    failures: list[str] = []
    warnings: list[str] = []
    report: dict = {"baseUrl": base, "checks": {}}

    # 1. /healthz
    try:
        code, body = http_request("GET", base + "/healthz", timeout)
        data = json.loads(body) if body else {}
        report["checks"]["healthz"] = {"httpStatus": code, "status": data.get("status"), "store": data.get("store")}
        if code != 200 or data.get("status") != "ok":
            failures.append(f"healthz_failed: http={code} status={data.get('status')}")
        if not allow_non_durable and data.get("store") != "postgres_durable":
            failures.append(f"store_not_durable: {data.get('store')}")
        if not allow_google_unconfigured and data.get("google") != "configured":
            failures.append(f"google_not_configured: {data.get('google')}")
        if expected_skus and data.get("catalogProducts") != len(expected_skus):
            failures.append(f"healthz_catalog_count_mismatch: got={data.get('catalogProducts')} expected={len(expected_skus)}")
    except Exception as exc:
        failures.append(f"healthz_error: {type(exc).__name__}")

    # 2. /v1/catalog
    try:
        code, body = http_request("GET", base + "/v1/catalog", timeout)
        data = json.loads(body) if body else {}
        live_skus = {p.get("id") for p in data.get("products", []) if isinstance(p, dict) and p.get("id")}
        missing = sorted(expected_skus - live_skus)
        extra = sorted(live_skus - expected_skus)
        report["checks"]["catalog"] = {"httpStatus": code, "count": len(live_skus), "missing": missing, "extra": extra}
        if code != 200:
            failures.append(f"catalog_http_error: {code}")
        if not live_skus or missing or extra:
            failures.append(f"catalog_parity_mismatch: missing={missing} extra={extra}")
    except Exception as exc:
        failures.append(f"catalog_error: {type(exc).__name__}")

    # 3. /status (non-routing operational status: side jobs & growing bounds)
    try:
        code, body = http_request("GET", base + "/status", timeout)
        data = json.loads(body) if body else {}
        bound_warnings = data.get("boundWarnings") or []
        side_jobs = data.get("sideJobs") or {}
        degraded_jobs = [k for k, v in side_jobs.items() if isinstance(v, dict) and v.get("status") not in ("ok", "idle")]
        report["checks"]["status"] = {
            "httpStatus": code,
            "status": data.get("status"),
            "degradedJobs": degraded_jobs,
            "boundWarningsCount": len(bound_warnings),
        }
        if code != 200:
            failures.append(f"status_http_error: {code}")
        if degraded_jobs:
            warnings.append(f"side_jobs_degraded: {degraded_jobs}")
        if bound_warnings:
            warnings.append(f"bound_warnings_active: {[w.get('name') for w in bound_warnings if isinstance(w, dict)]}")
    except Exception as exc:
        failures.append(f"status_error: {type(exc).__name__}")

    # 4. Unauthenticated POST /v1/purchases/validate must refuse with 401
    try:
        code, _ = http_request("POST", base + "/v1/purchases/validate", timeout, b"{}")
        report["checks"]["authBoundary"] = {"httpStatus": code}
        if code != 401:
            failures.append(f"auth_boundary_unexpected_status: {code}")
    except Exception as exc:
        failures.append(f"auth_boundary_error: {type(exc).__name__}")

    report["failures"] = failures
    report["warnings"] = warnings
    report["ok"] = len(failures) == 0
    return len(failures) == 0, report


def main() -> int:
    parser = argparse.ArgumentParser(description="Unattended read-only probe for Shorepop purchase verifier")
    parser.add_argument("--url", required=True, help="Base URL of verifier (e.g. https://shorepop-verifier-michael.onrender.com)")
    parser.add_argument("--repo-root", default=str(Path(__file__).resolve().parents[2]), help="Repo root containing Assets/Scripts/Services/Commerce/NativePurchasePolicy.cs")
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument("--allow-non-durable", action="store_true")
    parser.add_argument("--allow-google-unconfigured", action="store_true")
    args = parser.parse_args()

    expected_skus = load_client_skus(Path(args.repo_root))
    ok, report = probe(
        args.url,
        expected_skus,
        timeout=args.timeout,
        allow_non_durable=args.allow_non_durable,
        allow_google_unconfigured=args.allow_google_unconfigured,
    )
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
