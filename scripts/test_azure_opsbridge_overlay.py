"""Release contract for the AKS OpsBridge API route and image."""

import pathlib
import subprocess
import unittest

import yaml


ROOT = pathlib.Path(__file__).resolve().parents[1]
OVERLAY = ROOT / "infrastructure/kubernetes/overlays/azure-cloud"
SOURCE_SHA = "3e7fb0d30dd2a5caffd3a7b9c73ec24367e11bf0"


class AzureOpsBridgeOverlayTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        rendered = subprocess.check_output(["kubectl", "kustomize", str(OVERLAY)], text=True)
        cls.objects = {
            (item["kind"], item["metadata"]["name"]): item
            for item in yaml.safe_load_all(rendered)
            if item
        }

    def test_api_is_pinned_to_published_source(self):
        deployment = self.objects[("Deployment", "opsbridge-api")]
        image = deployment["spec"]["template"]["spec"]["containers"][0]["image"]
        self.assertEqual(image, f"acrflashsalep6.azurecr.io/opsbridge-api:{SOURCE_SHA}")
        self.assertIn(("Service", "opsbridge-api"), self.objects)
        # A migration must run and complete before this Deployment rolls. It is
        # applied separately, so Argo cannot race the migration with the API.
        self.assertNotIn(("Job", "opsbridge-migrate"), self.objects)

    def test_public_opsbridge_paths_reach_opsbridge_api(self):
        route = self.objects[("HTTPRoute", "flashsale")]
        paths = {
            match["path"]["value"]: (rule["backendRefs"][0]["name"], rule)
            for rule in route["spec"]["rules"]
            for match in rule.get("matches", [])
        }
        self.assertEqual(paths["/opsbridge/api"][0], "opsbridge-api")
        self.assertEqual(paths["/opsbridge/healthz"][0], "opsbridge-api")
        health_rule = paths["/opsbridge/healthz"][1]
        self.assertEqual(
            health_rule["filters"][0]["urlRewrite"]["path"]["replacePrefixMatch"],
            "/healthz",
        )


if __name__ == "__main__":
    unittest.main()
