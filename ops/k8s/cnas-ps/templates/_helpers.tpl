{{/*
=============================================================================
Helper templates for the cnas-ps chart.
=============================================================================
All shared names, labels, and selectors live here so resource manifests
stay declarative. Keep these definitions stable — renaming a label key is
effectively a breaking change because Kubernetes selectors are immutable
after the resource is created.
=============================================================================
*/}}

{{/* ---------------------------------------------------------------------
     Chart name (truncated for K8s 63-char DNS-1123 label limit).
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* ---------------------------------------------------------------------
     Fully qualified release name: {release}-{chart} or override.
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* ---------------------------------------------------------------------
     Chart label (name + version) for traceability.
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* ---------------------------------------------------------------------
     Common labels — applied to every resource for grouping and pruning.
     `helm.sh/chart` + `app.kubernetes.io/managed-by` let `helm uninstall`
     prune resources reliably even after a chart rename.
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.labels" -}}
helm.sh/chart: {{ include "cnas-ps.chart" . }}
{{ include "cnas-ps.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- with .Values.commonLabels }}
{{ toYaml . }}
{{- end }}
{{- end -}}

{{/* ---------------------------------------------------------------------
     Selector labels — immutable subset used by Services / Deployments /
     NetworkPolicies. NEVER include chart version or app version here —
     selectors are immutable.
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.selectorLabels" -}}
app.kubernetes.io/name: {{ include "cnas-ps.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{/* ---------------------------------------------------------------------
     Component-specific selector labels.
     Usage: {{ include "cnas-ps.componentSelectorLabels" (dict "ctx" . "component" "api") }}
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.componentSelectorLabels" -}}
{{ include "cnas-ps.selectorLabels" .ctx }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{/* ---------------------------------------------------------------------
     Component-specific full label set.
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.componentLabels" -}}
{{ include "cnas-ps.labels" .ctx }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{/* ---------------------------------------------------------------------
     ServiceAccount name resolver. Returns the configured override when
     `serviceAccount.create` is true, else the release default.
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "cnas-ps.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/* ---------------------------------------------------------------------
     Component-prefixed resource names. Helm truncates to 63 chars
     defensively in case `Release.Name` is already long.
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.apiName" -}}
{{- printf "%s-api" (include "cnas-ps.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "cnas-ps.apiConfigName" -}}
{{- printf "%s-api-config" (include "cnas-ps.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "cnas-ps.apiSecretName" -}}
{{- printf "%s-api-secret" (include "cnas-ps.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "cnas-ps.postgresName" -}}
{{- printf "%s-postgres" (include "cnas-ps.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "cnas-ps.postgresConfigName" -}}
{{- printf "%s-postgres-config" (include "cnas-ps.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "cnas-ps.postgresHeadlessServiceName" -}}
{{- printf "%s-postgres-headless" (include "cnas-ps.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "cnas-ps.postgresRwServiceName" -}}
{{- printf "%s-postgres-rw" (include "cnas-ps.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "cnas-ps.minioName" -}}
{{- printf "%s-minio" (include "cnas-ps.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* ---------------------------------------------------------------------
     Validation — fail fast at install time on missing required values.
     The chart deliberately does NOT default these — see values.yaml
     comments for the rationale (mutable tags = production rollback hazard;
     baked-in hostnames = ops-portability hazard).
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.validateRequired" -}}
{{- if not .Values.image.tag -}}
{{- fail "image.tag is required. Set --set image.tag=<sha-or-version> at install time." -}}
{{- end -}}
{{- if and .Values.ingress.enabled (not .Values.ingress.host) -}}
{{- fail "ingress.host is required when ingress.enabled=true. Set --set ingress.host=cnas.example.gov.md." -}}
{{- end -}}
{{- end -}}
