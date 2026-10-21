"""Regression tests for the concurrency gate's verdict."""

import unittest

from oversell_demo import Summary, audit_failures, resync_applied


class ConcurrencyAuditTests(unittest.TestCase):
    def test_exact_fill_passes(self):
        summary = Summary(total=50, accepted=10, rejected=40,
                          status_counts={"409": 40})
        self.assertEqual([], audit_failures(summary, 50, 10, 1, 0, 10))

    def test_conserved_but_underfilled_sale_fails(self):
        summary = Summary(total=50, accepted=3, rejected=47,
                          status_counts={"409": 47})
        self.assertTrue(audit_failures(summary, 50, 10, 1, 7, 3))

    def test_persisted_order_mismatch_fails(self):
        summary = Summary(total=50, accepted=10, rejected=40,
                          status_counts={"409": 40})
        self.assertTrue(audit_failures(summary, 50, 10, 1, 0, 9))

    def test_non_stock_rejections_fail(self):
        summary = Summary(total=50, accepted=10, rejected=40,
                          status_counts={"400": 1, "409": 39})
        self.assertTrue(audit_failures(summary, 50, 10, 1, 0, 10))

    def test_dry_run_response_cannot_pass_resync_gate(self):
        self.assertFalse(resync_applied({"postgres": 10, "redis": 3, "fixed": False}, 10))

    def test_already_aligned_mirror_needs_no_write(self):
        self.assertTrue(resync_applied({"postgres": 10, "redis": 10, "fixed": False}, 10))


if __name__ == "__main__":
    unittest.main()
