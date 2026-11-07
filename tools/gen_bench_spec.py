#!/usr/bin/env python3
"""Generate a large synthetic OpenAPI 3.0 spec pair for benchmarking.

usage: gen_bench_spec.py OUT_DIR [N_RESOURCES]

Writes base.yaml / revision.yaml (and .json twins). The revision mutates roughly one resource
in ten: removed and added properties, type and enum changes, new required fields, removed
endpoints and responses, so every stage of the pipeline (diff, checks, formatting) does work.
"""
import json
import random
import sys

out = sys.argv[1]
n = int(sys.argv[2]) if len(sys.argv) > 2 else 400
rnd = random.Random(42)


def schema(i, rev):
    props = {
        "id": {"type": "string", "format": "uuid"},
        "name": {"type": "string", "maxLength": 128, "description": f"name of item {i}"},
        "count": {"type": "integer", "minimum": 0, "maximum": 1000},
        "status": {"type": "string", "enum": ["active", "inactive", "pending", "archived"]},
        "tags": {"type": "array", "items": {"type": "string"}, "maxItems": 20},
        "owner": {"$ref": f"#/components/schemas/Owner{i % 50}"},
        "meta": {
            "type": "object",
            "properties": {
                "created": {"type": "string", "format": "date-time"},
                "updated": {"type": "string", "format": "date-time"},
                "version": {"type": "integer"},
            },
        },
    }
    required = ["id", "name"]
    if rev and i % 10 == 3:
        del props["count"]
    if rev and i % 10 == 5:
        props["status"]["enum"] = ["active", "inactive"]
    if rev and i % 10 == 7:
        props["name"]["maxLength"] = 64
        required.append("status")
    if rev and i % 10 == 9:
        props["count"]["type"] = "string"
        props["extra"] = {"type": "boolean"}
    return {"type": "object", "required": required, "properties": props}


def owner(i, rev):
    s = {"type": "object", "properties": {"login": {"type": "string"}, "email": {"type": "string", "format": "email"}}}
    if rev and i % 7 == 0:
        s["required"] = ["login"]
    return s


def spec(rev):
    paths = {}
    for i in range(n):
        if rev and i % 25 == 0:
            continue  # removed resource
        item = f"#/components/schemas/Item{i}"
        params = [
            {"name": "limit", "in": "query", "schema": {"type": "integer", "maximum": 100 if not (rev and i % 10 == 1) else 50}},
            {"name": "x-request-id", "in": "header", "schema": {"type": "string"}},
        ]
        responses = {
            "200": {"description": "ok", "content": {"application/json": {"schema": {"type": "array", "items": {"$ref": item}}}}},
            "400": {"description": "bad request"},
        }
        if rev and i % 10 == 2:
            del responses["400"]
        paths[f"/v1/resource{i}"] = {
            "get": {"operationId": f"list{i}", "tags": [f"group{i % 12}"], "parameters": params, "responses": responses},
            "post": {
                "operationId": f"create{i}",
                "requestBody": {"required": True, "content": {"application/json": {"schema": {"$ref": item}}}},
                "responses": {"201": {"description": "created", "content": {"application/json": {"schema": {"$ref": item}}}}},
            },
        }
        paths[f"/v1/resource{i}/{{id}}"] = {
            "parameters": [{"name": "id", "in": "path", "required": True, "schema": {"type": "string"}}],
            "get": {"operationId": f"get{i}", "responses": {"200": {"description": "ok", "content": {"application/json": {"schema": {"$ref": item}}}}}},
            "delete": {"operationId": f"delete{i}", "deprecated": bool(rev and i % 10 == 4), "responses": {"204": {"description": "deleted"}}},
        }
    if rev:
        for i in range(n, n + n // 20):
            paths[f"/v2/new{i}"] = {"get": {"operationId": f"new{i}", "responses": {"200": {"description": "ok"}}}}
    schemas = {f"Item{i}": schema(i, rev) for i in range(n)}
    schemas.update({f"Owner{i}": owner(i, rev) for i in range(50)})
    return {
        "openapi": "3.0.3",
        "info": {"title": "bench", "version": "2.0.0" if rev else "1.0.0"},
        "paths": paths,
        "components": {"schemas": schemas},
    }


def to_yaml(v, indent=0):
    sp = "  " * indent
    lines = []
    if isinstance(v, dict):
        for k, x in v.items():
            key = json.dumps(k) if (k.startswith("/") or k.startswith("$") or k[0].isdigit()) else k
            if isinstance(x, (dict, list)) and x:
                lines.append(f"{sp}{key}:")
                lines.append(to_yaml(x, indent + 1))
            else:
                lines.append(f"{sp}{key}: {scalar(x)}")
    else:
        for x in v:
            if isinstance(x, dict) and x:
                inner = to_yaml(x, indent + 1).split("\n")
                lines.append(f"{sp}- {inner[0].strip()}")
                lines.extend(inner[1:])
            else:
                lines.append(f"{sp}- {scalar(x)}")
    return "\n".join(lines)


def scalar(x):
    if x == {}:
        return "{}"
    if x == []:
        return "[]"
    if isinstance(x, bool):
        return "true" if x else "false"
    if isinstance(x, (int, float)):
        return str(x)
    return json.dumps(x)


for name, rev in (("base", False), ("revision", True)):
    s = spec(rev)
    with open(f"{out}/{name}.yaml", "w", encoding="utf-8", newline="\n") as f:
        f.write(to_yaml(s) + "\n")
    with open(f"{out}/{name}.json", "w", encoding="utf-8", newline="\n") as f:
        json.dump(s, f)
