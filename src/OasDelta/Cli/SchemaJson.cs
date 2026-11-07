namespace OasDelta.Cli;

/// <summary>Output of upstream "oasdiff schema": the JSON Schema of the json changelog output.</summary>
internal static class SchemaJson
{
    public const string Text = """
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://github.com/oasdiff/oasdiff/formatters/changes",
  "$ref": "#/$defs/Changes",
  "$defs": {
    "Change": {
      "properties": {
        "id": {
          "type": "string"
        },
        "text": {
          "type": "string"
        },
        "comment": {
          "type": "string"
        },
        "disclaimers": {
          "items": {
            "type": "string"
          },
          "type": "array"
        },
        "sharedSchema": {
          "$ref": "#/$defs/SharedSchema"
        },
        "level": {
          "type": "integer"
        },
        "operation": {
          "type": "string"
        },
        "operationId": {
          "type": "string"
        },
        "path": {
          "type": "string"
        },
        "section": {
          "type": "string"
        },
        "attributes": {
          "type": "object"
        },
        "baseSource": {
          "$ref": "#/$defs/Source"
        },
        "revisionSource": {
          "$ref": "#/$defs/Source"
        },
        "fingerprint": {
          "type": "string"
        }
      },
      "additionalProperties": false,
      "type": "object",
      "required": [
        "level"
      ]
    },
    "Changes": {
      "items": {
        "$ref": "#/$defs/Change"
      },
      "type": "array"
    },
    "SharedSchema": {
      "properties": {
        "name": {
          "type": "string"
        },
        "properties": {
          "items": {
            "type": "string"
          },
          "type": "array"
        },
        "count": {
          "type": "integer"
        },
        "cyclic": {
          "type": "boolean"
        }
      },
      "additionalProperties": false,
      "type": "object",
      "required": [
        "properties"
      ]
    },
    "Source": {
      "properties": {
        "file": {
          "type": "string"
        },
        "line": {
          "type": "integer"
        },
        "column": {
          "type": "integer"
        },
        "endLine": {
          "type": "integer"
        },
        "endColumn": {
          "type": "integer"
        }
      },
      "additionalProperties": false,
      "type": "object"
    }
  }
}
""";
}
