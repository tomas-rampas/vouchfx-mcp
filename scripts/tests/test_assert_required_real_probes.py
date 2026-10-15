"""Inert TRX/console evidence checks; no CLI or Docker invocation."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

SPEC = importlib.util.spec_from_file_location(
    "assert_required_real_probes", Path(__file__).resolve().parents[1] / "assert-required-real-probes.py")
GATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATE)


class RequiredRealProbeAssertionTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="mcp-required-contract-")
        self.addCleanup(self.scratch.cleanup)
        self.trx = Path(self.scratch.name) / "results.trx"
        self.log = Path(self.scratch.name) / "console.log"
        self.root = ET.Element("TestRun")
        ET.SubElement(self.root, "Counters", total="6", executed="6", passed="6", failed="0", notExecuted="0")
        self.results = ET.SubElement(self.root, "Results")
        for method in sorted(GATE.METHODS):
            row = ET.SubElement(self.results, "UnitTestResult", testName=method, outcome="Passed")
            ET.SubElement(ET.SubElement(row, "Output"), "StdOut").text = GATE.STORAGE_EVIDENCE.get(method, "")
        self.lines = ["MEASURED pair: exitCode=4, stepless Inconclusive, value absent."] * 11
        self.lines += ["MEASURED: required real probe completed; process exit 4, 3 events."] * 2
        self.lines += ["termination=CompletedNormally, exitCode=4, eventsFileWritten=True"]
        self.lines += list(GATE.STORAGE_EVIDENCE.values())

    def verify(self):
        ET.ElementTree(self.root).write(self.trx, encoding="utf-8", xml_declaration=True)
        self.log.write_text("\n".join(self.lines), encoding="utf-8")
        return GATE.verify(self.trx, self.log)

    def row(self, method):
        return next(row for row in self.results if row.get("testName") == method)

    def test_all_six_completed_rows_and_all_execution_markers_are_accepted(self):
        result = self.verify()
        self.assertEqual(6, result["requiredMethods"])
        self.assertEqual(14, result["measuredCliInvocations"])
        self.assertEqual(2, result["measuredStorageGenerations"])

    def test_either_missing_storage_fact_is_rejected(self):
        for method in GATE.STORAGE_EVIDENCE:
            with self.subTest(method=method):
                row = self.row(method)
                self.results.remove(row)
                with self.assertRaisesRegex(SystemExit, "Exactly six"):
                    self.verify()
                self.results.append(row)

    def test_either_skipped_storage_fact_is_rejected(self):
        for method in GATE.STORAGE_EVIDENCE:
            with self.subTest(method=method):
                row = self.row(method)
                row.set("outcome", "NotExecuted")
                with self.assertRaisesRegex(SystemExit, "failed or skipped"):
                    self.verify()
                row.set("outcome", "Passed")

    def test_either_silent_pass_without_row_evidence_is_rejected(self):
        for method in GATE.STORAGE_EVIDENCE:
            with self.subTest(method=method):
                output = self.row(method).find("Output/StdOut")
                old = output.text
                output.text = ""
                with self.assertRaisesRegex(SystemExit, "storage generation/validation evidence missing"):
                    self.verify()
                output.text = old

    def test_storage_console_evidence_cannot_be_missing(self):
        self.lines.remove(next(iter(GATE.STORAGE_EVIDENCE.values())))
        with self.assertRaisesRegex(SystemExit, "storage generation/validation evidence missing"):
            self.verify()

    def test_existing_fourteen_run_evidence_remains_required(self):
        self.lines.pop(0)
        with self.assertRaisesRegex(SystemExit, "Eleven measured"):
            self.verify()

    def test_old_four_method_counter_set_is_rejected(self):
        self.root.find("Counters").set("executed", "4")
        with self.assertRaisesRegex(SystemExit, "counters differ"):
            self.verify()

    def test_workflow_filter_matches_the_six_expected_methods(self):
        workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/required-real-probes.yml").read_text()
        selection = next(line.strip()[len("filter='"):-1] for line in workflow.splitlines() if line.strip().startswith("filter='"))
        methods = selection.split("|FullyQualifiedName=")
        methods[0] = methods[0].removeprefix("FullyQualifiedName=")
        self.assertEqual(GATE.METHODS, set(methods))
        self.assertEqual(6, len(methods))


if __name__ == "__main__":
    unittest.main()
