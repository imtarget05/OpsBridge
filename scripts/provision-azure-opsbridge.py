#!/usr/bin/env python3
"""Bootstrap OpsBridge DB roles and Kubernetes Secrets in the AKS dev cluster.

No credential is placed in argv, a tracked file, or command output. This is a
one-time bootstrap: existing Secrets cause a refusal instead of silent rotation.
"""

import base64
import json
import secrets
import subprocess
import sys


CONTEXT = "aks-portfolio-dev"
NAMESPACE = "flashsale"
DATABASE = "OpsBridgeDb"


def kubectl(*args: str, input_text: str | None = None) -> str:
    result = subprocess.run(
        ["kubectl", "--context", CONTEXT, *args],
        input=input_text,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode:
        # kubectl errors must not contain stdin, but still avoid echoing them
        # because a failed external process may log its own arguments.
        raise RuntimeError(f"kubectl command failed (exit {result.returncode})")
    return result.stdout


def psql(database: str, sql: str) -> None:
    kubectl(
        "-n", NAMESPACE, "exec", "-i", "postgres-0", "--",
        "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
        "-d", database,
        input_text=sql,
    )


def secret_exists(name: str) -> bool:
    result = subprocess.run(
        ["kubectl", "--context", CONTEXT, "-n", NAMESPACE, "get", "secret", name, "-o", "name"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    return result.returncode == 0


def create_secret(name: str, values: dict[str, str]) -> None:
    payload = {
        "apiVersion": "v1",
        "kind": "Secret",
        "metadata": {"name": name, "namespace": NAMESPACE},
        "type": "Opaque",
        "stringData": values,
    }
    # `apply` would store stringData in last-applied-configuration annotations.
    # Create has no such annotation and we already refuse to overwrite Secrets.
    kubectl("create", "-f", "-", input_text=json.dumps(payload))


def main() -> int:
    if len(sys.argv) != 2 or sys.argv[1] != "--apply":
        print("Usage: python3 scripts/provision-azure-opsbridge.py --apply")
        return 2
    if secret_exists("opsbridge-secrets") or secret_exists("opsbridge-migrate-secrets"):
        print("Refusing to rotate existing OpsBridge Secrets.", file=sys.stderr)
        return 1

    source = json.loads(kubectl("-n", NAMESPACE, "get", "secret", "flashsale-secrets", "-o", "json"))
    rabbitmq_uri = base64.b64decode(source["data"]["rabbitmq-connection"]).decode("utf-8")
    ddl_password = secrets.token_hex(32)
    dml_password = secrets.token_hex(32)
    jwt_key = secrets.token_urlsafe(48)

    # All identifiers are fixed literals; generated passwords contain only hex.
    psql("postgres", (
        f"CREATE ROLE opsbridge_migrator LOGIN PASSWORD '{ddl_password}';\n"
        f"CREATE ROLE opsbridge_runtime LOGIN PASSWORD '{dml_password}';\n"
    ))
    psql("postgres", f'CREATE DATABASE "{DATABASE}" OWNER opsbridge_migrator;\n')
    psql(DATABASE, (
        "GRANT CONNECT ON DATABASE \"OpsBridgeDb\" TO opsbridge_runtime;\n"
        "GRANT USAGE ON SCHEMA public TO opsbridge_runtime;\n"
        "ALTER DEFAULT PRIVILEGES FOR ROLE opsbridge_migrator IN SCHEMA public "
        "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO opsbridge_runtime;\n"
        "ALTER DEFAULT PRIVILEGES FOR ROLE opsbridge_migrator IN SCHEMA public "
        "GRANT USAGE, SELECT ON SEQUENCES TO opsbridge_runtime;\n"
    ))

    host = f"Host=postgres;Port=5432;Database={DATABASE};"
    create_secret("opsbridge-migrate-secrets", {
        "pg-migration-connection": host + f"Username=opsbridge_migrator;Password={ddl_password}",
    })
    create_secret("opsbridge-secrets", {
        "pg-connection": host + f"Username=opsbridge_runtime;Password={dml_password}",
        "rabbitmq-connection": rabbitmq_uri,
        "jwt-signing-key": jwt_key,
    })
    print("Created OpsBridge database, separate DDL/DML roles, and two AKS Secrets.")
    print("No credential values were printed or stored in Git.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, KeyError, ValueError) as exc:
        print(f"Bootstrap failed: {type(exc).__name__}", file=sys.stderr)
        raise SystemExit(1)
