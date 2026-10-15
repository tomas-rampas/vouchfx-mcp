"""Require six real CLI probes, fourteen measured engine runs and two storage generations."""
import argparse
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

METHODS = {
    "Vouchfx.Mcp.Tests.RealEnvRefusalAgainstPinnedCliTests.DependencyEnvRefusal_AgainstPinnedInstalledCli_StillPrintsTheSignature_AndStillOmitsTheValue",
    "Vouchfx.Mcp.Tests.RealReservedEnvironmentAgainstPinnedCliTests.CompleteEngineOwnedTable_AndEveryTypeKeyRefusal_AgreeWithThePinnedCli",
    "Vouchfx.Mcp.Tests.RealStepAttemptEnvelopeAgainstPinnedCliTests.StepAttemptEvents_FromARealPinnedEngineRun_CarryTheEnvelopeThisServersDocumentationClaims",
    "Vouchfx.Mcp.Tests.RealStepAttemptEnvelopeAgainstPinnedCliTests.AnImmediateStep_EmitsNoAttemptEvent_SoItsTimelineIsEmptyAndItsVerifyModeNull",
    "Vouchfx.Mcp.Tests.RealScaffoldSuiteAgainstPinnedCliTests.StorageScaffold_AgainstPinnedInstalledCli_EmitsS3AndPassesFullValidation",
    "Vouchfx.Mcp.Tests.RealScaffoldSuiteAgainstPinnedCliTests.StorageScaffold_WithExplicitS3Outline_AgainstPinnedInstalledCli_PassesFullValidation",
}


STORAGE_EVIDENCE = {
    "Vouchfx.Mcp.Tests.RealScaffoldSuiteAgainstPinnedCliTests.StorageScaffold_AgainstPinnedInstalledCli_EmitsS3AndPassesFullValidation": "MEASURED storage catalogue example: emitted s3; full validation valid; no errors or D-1205.",
    "Vouchfx.Mcp.Tests.RealScaffoldSuiteAgainstPinnedCliTests.StorageScaffold_WithExplicitS3Outline_AgainstPinnedInstalledCli_PassesFullValidation": "MEASURED storage explicit scaffold: emitted s3; full validation valid; no errors or D-1205."
}


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def verify(trx_path, log_path):
    root = ET.fromstring(Path(trx_path).read_bytes())
    counters = root.find(".//{*}Counters")
    results = root.findall(".//{*}UnitTestResult")
    require(counters is not None, "Required probes have no TRX counters.")
    require(len(results) == 6, "Exactly six required probe results must be present.")
    require({r.get("testName") for r in results} == METHODS, "Required method identities differ.")
    require(all(r.get("outcome") == "Passed" for r in results), "A required probe failed or skipped.")
    expected = {"total": 6, "executed": 6, "passed": 6, "failed": 0, "notExecuted": 0}
    require(all(int(counters.get(k, "-1")) == v for k, v in expected.items()), "Required TRX counters differ.")
    log = Path(log_path).read_text(encoding="utf-8-sig")
    require("SKIPPED (not a failure)" not in log and "Required real probe evidence missing" not in log, "A required probe returned without execution evidence.")
    reserved = re.findall(r"MEASURED [^\r\n]+: exitCode=4, stepless Inconclusive, value absent\.", log)
    attempts = re.findall(r"MEASURED: required real probe completed; process exit 4, [0-9]+ events\.", log)
    require(len(reserved) == 11, "Eleven measured reserved-environment CLI refusals are required.")
    require(len(attempts) == 2, "Both measured StepAttempt CLI runs are required.")
    require("termination=CompletedNormally, exitCode=4, eventsFileWritten=True" in log, "Measured dependency-environment CLI refusal is missing.")
    for method, marker in STORAGE_EVIDENCE.items():
        row = next(result for result in results if result.get("testName") == method)
        require(marker in "".join(row.itertext()) and marker in log,
                "Required storage generation/validation evidence missing: " + method)
    return {"requiredMethods": 6, "measuredCliInvocations": 14, "measuredStorageGenerations": 2, "passed": 6, "failed": 0, "skipped": 0}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trx", type=Path)
    parser.add_argument("log", type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.trx, args.log)))


if __name__ == "__main__":
    main()
