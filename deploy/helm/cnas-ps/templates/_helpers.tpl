{{/*
=============================================================================
Helper templates for the cnas-ps chart.
=============================================================================
All shared names, labels, and selectors live here so resource manifests
stay declarative. Keep these definitions stable — renaming a label key is
effectively a breaking change because Kubernetes selectors are immutable.
=============================================================================
*/}}

{{/* ---------------------------------------------------------------------
     Chart name (truncated for K8s 63-char DNS limit).
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
     Selector labels — immutable subset used by Services / Deployments.
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
     ServiceAccount name resolver.
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "cnas-ps.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/* ---------------------------------------------------------------------
     Component-prefixed resource names.
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.apiName" -}}
{{- printf "%s-api" (include "cnas-ps.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "cnas-ps.webName" -}}
{{- printf "%s-web" (include "cnas-ps.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "cnas-ps.apiConfigName" -}}
{{- printf "%s-api-config" (include "cnas-ps.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* ---------------------------------------------------------------------
     Validation — fail fast if image tags are missing.
--------------------------------------------------------------------- */}}
{{- define "cnas-ps.validateImages" -}}
{{- if not .Values.api.image.tag -}}
{{- fail "api.image.tag is required. Set it on the command line: --set api.image.tag=<sha-or-version>" -}}
{{- end -}}
{{- if not .Values.web.image.tag -}}
{{- fail "web.image.tag is required. Set it on the command line: --set web.image.tag=<sha-or-version>" -}}
{{- end -}}
{{- end -}}
