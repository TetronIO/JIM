# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
#
# The stand-in WinRM server for the remoting harness (#2021). Test infrastructure only: never shipped.
#
# The .NET SDK image is the base because it carries PowerShell 7.6, the version Tetron.PowerShellRemoting hosts, and
# JIM already pins it. Python packages come from Ubuntu where Ubuntu has them; pyspnego and its krb5 dependency come
# from PyPI, hash-pinned. krb5 publishes no Linux wheels, so it is compiled from its hash-pinned source in a build
# stage, with its build tools hash-pinned too.

ARG BASE_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa

FROM ${BASE_IMAGE} AS krb5-build
# EXTRA_CA_CERTS_BASE64: an optional PEM bundle for TLS-intercepting proxies, as JIM's own images accept.
ARG EXTRA_CA_CERTS_BASE64=""
RUN if [ -n "$EXTRA_CA_CERTS_BASE64" ]; then echo "$EXTRA_CA_CERTS_BASE64" | base64 -d > /usr/local/share/ca-certificates/extra.crt && update-ca-certificates; fi
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3-dev=3.12.3-0ubuntu2.1 python3-pip=24.0+dfsg-1ubuntu1.3 gcc=4:13.2.0-7ubuntu1 libkrb5-dev=1.20.1-6ubuntu2.10 \
    && rm -rf /var/lib/apt/lists/*
COPY requirements-build.txt requirements-sdist.txt /build/
RUN pip3 install --break-system-packages --no-cache-dir --require-hashes -r /build/requirements-build.txt
RUN pip3 download --no-deps --no-binary :all: --require-hashes -d /build/src -r /build/requirements-sdist.txt
RUN pip3 wheel --no-deps --no-build-isolation -w /build/wheels /build/src/krb5-0.10.0.tar.gz

FROM ${BASE_IMAGE}
ARG EXTRA_CA_CERTS_BASE64=""
RUN if [ -n "$EXTRA_CA_CERTS_BASE64" ]; then echo "$EXTRA_CA_CERTS_BASE64" | base64 -d > /usr/local/share/ca-certificates/extra.crt && update-ca-certificates; fi
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3=3.12.3-0ubuntu2.1 python3-pip=24.0+dfsg-1ubuntu1.3 \
        python3-gssapi=1.8.2-1ubuntu1 python3-cryptography=41.0.7-4ubuntu0.4 libkrb5-3=1.20.1-6ubuntu2.10 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=krb5-build /build/wheels /wheels
COPY requirements.txt /app/
RUN pip3 install --break-system-packages --no-cache-dir --no-deps /wheels/krb5-0.10.0-*.whl \
    && pip3 install --break-system-packages --no-cache-dir --no-deps --require-hashes -r /app/requirements.txt
COPY winrm_server.py /app/
ENV KRB5_CONFIG=/etc/harness/krb5.conf \
    KRB5_KTNAME=FILE:/etc/harness/winrm-harness.keytab \
    PYTHONUNBUFFERED=1 \
    POWERSHELL_TELEMETRY_OPTOUT=1
EXPOSE 5985 5986
CMD ["python3", "/app/winrm_server.py"]
