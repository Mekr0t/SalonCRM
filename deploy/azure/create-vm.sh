#!/usr/bin/env bash
# Creates the SalonCRM VM on Azure (Azure for Students) — SPEC §11.1.
# Usage: az login && ./deploy/azure/create-vm.sh
# Override defaults via env, e.g. DNS_LABEL=saloncrm-xyz ./deploy/azure/create-vm.sh
set -euo pipefail

RG="${RG:-saloncrm-rg}"
# Azure for Students allows only a few EU regions; B-series sizes were available only in denmarkeast.
LOCATION="${LOCATION:-denmarkeast}"
VM="${VM:-saloncrm-vm}"
SIZE="${SIZE:-Standard_B2als_v2}"   # 2 vCPU / 4 GB — enough to build .NET + Node images
DNS_LABEL="${DNS_LABEL:-saloncrm-tuke}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

az group create -n "$RG" -l "$LOCATION" -o none

az vm create \
  -g "$RG" -n "$VM" -l "$LOCATION" \
  --image Ubuntu2404 \
  --size "$SIZE" \
  --admin-username azureuser \
  --generate-ssh-keys \
  --public-ip-sku Standard \
  --public-ip-address-dns-name "$DNS_LABEL" \
  --nsg "$VM-nsg" \
  --nsg-rule SSH \
  --os-disk-size-gb 30 \
  --custom-data "$SCRIPT_DIR/cloud-init.yml" \
  -o table

# NSG: only 22 (created above), 8080 (app) and 8000 (ml). DB ports stay closed.
az network nsg rule create -g "$RG" --nsg-name "$VM-nsg" -n allow-app-ml \
  --priority 1010 --access Allow --protocol Tcp --direction Inbound \
  --destination-port-ranges 8080 8000 -o none

az vm show -d -g "$RG" -n "$VM" --query "{fqdn:fqdns, ip:publicIps}" -o table
