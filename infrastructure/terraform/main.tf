terraform {
  required_version = ">= 1.5"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
  # Remote state lives in an Azure Storage backend (azurerm). Create the
  # storage account once (see infrastructure/terraform/backup-storage/), then:
  #   terraform init -backend-config="storage_account_name=<acct>" ...
  backend "azurerm" {
    resource_group_name  = "rg-tfstate"
    storage_account_name = "<tfstate-account>"
    container_name       = "tfstate"
    key                  = "flashsale.terraform.tfstate"
  }
}

provider "azurerm" {
  features {}
}

# ---------------------------------------------------------------
# Foundation
# ---------------------------------------------------------------
resource "azurerm_resource_group" "rg" {
  name     = var.resource_group_name
  location = var.location
}

resource "random_string" "suffix" {
  length  = 6
  special = false
  upper   = false
}

# ---------------------------------------------------------------
# Container Registry (images for api + worker)
# ---------------------------------------------------------------
resource "azurerm_container_registry" "acr" {
  name                = "acrflashsale${random_string.suffix.result}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  sku                 = "Basic"
  admin_enabled       = false
}


# ---------------------------------------------------------------
# OIDC federation: AKS workload identity pulls from ACR
# AKS kubelet / workload identity uses OIDC to acquire an ACR pull
# token without admin_enabled or a client secret (ADR-011).
# ---------------------------------------------------------------
resource "azurerm_user_assigned_identity" "aks_acr_pull" {
  name                = "id-aks-acr-pull"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
}

# AKS OIDC issuer (configured when the AKS cluster is created; the issuer URL
# is the cluster's OIDC endpoint, e.g. https://<cluster>.region.aks.io/).
resource "azurerm_federated_identity_credential" "aks_acr_pull" {
  name                = "aks-acr-pull"
  resource_group_name = azurerm_resource_group.rg.name
  parent_id           = azurerm_user_assigned_identity.aks_acr_pull.id
  audience            = ["api://AzureADTokenExchange"]
  issuer              = var.aks_oidc_issuer_url
  subject             = "system:serviceaccount:flash-sale-prod:default"
}

# AcrPull so the AKS identity can pull images from this ACR.
resource "azurerm_role_assignment" "aks_acr_pull" {
  scope                = azurerm_container_registry.acr.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.aks_acr_pull.principal_id
}

# ---------------------------------------------------------------
# Observability (Phase 10 landing zone)
# ---------------------------------------------------------------
resource "azurerm_log_analytics_workspace" "law" {
  name                = "law-flashsale"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

# ---------------------------------------------------------------
# PostgreSQL Flexible Server (source of truth)
# ---------------------------------------------------------------
resource "random_password" "pg" {
  length           = 24
  special          = true
  override_special = "-_"
}

resource "azurerm_postgresql_flexible_server" "db" {
  name                   = "psql-flashsale-${random_string.suffix.result}"
  resource_group_name    = azurerm_resource_group.rg.name
  location               = azurerm_resource_group.rg.location
  version                = "15"
  administrator_login    = "psqladmin"
  administrator_password = coalesce(var.postgres_admin_password, random_password.pg.result)
  storage_mb             = 32768
  sku_name               = var.enable_postgres_ha ? "GP_Standard_D2s_v3" : var.postgres_sku_name

  dynamic "high_availability" {
    for_each = var.enable_postgres_ha ? [1] : []
    content {
      mode = "ZoneRedundant"
    }
  }

  # Phase 4 lesson: reserve headroom above the app pool (80).
  # 200 max connections leaves room for migrations, operators and the worker.
}

resource "azurerm_postgresql_flexible_server_database" "flashsaledb" {
  charset   = "UTF8"
  collation = "en_US.utf8"
  name      = "FlashSaleDb"
  server_id = azurerm_postgresql_flexible_server.db.id
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "allow_azure" {
  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.db.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# ---------------------------------------------------------------
# Redis (fast-fail reservation tier, ADR-003)
# ---------------------------------------------------------------
resource "azurerm_redis_cache" "reservation" {
  name                = "redis-flashsale-${random_string.suffix.result}"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  capacity            = var.redis_sku == "Premium" ? 1 : (var.redis_sku == "Standard" ? 1 : 0)
  family              = var.redis_sku == "Premium" ? "P" : "C"
  sku_name            = var.redis_sku

  redis_configuration {
    maxmemory_policy = "noeviction" # never silently drop reservation state
  }
}

# ---------------------------------------------------------------
# Azure Service Bus (async fulfillment, ADR-004)
# ---------------------------------------------------------------
resource "azurerm_servicebus_namespace" "orders" {
  name                = "sb-flashsale-${random_string.suffix.result}"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  sku                 = "Standard"
}

resource "azurerm_servicebus_queue" "orders" {
  name         = "orders"
  namespace_id = azurerm_servicebus_namespace.orders.id

  lock_duration                        = "PT1M"
  max_delivery_count                   = 4 # mirrors OrderProcessor.MaxAttempts
  dead_lettering_on_message_expiration = false
}

# ---------------------------------------------------------------
# Azure Container Apps Environment (Phase 7 compute)
# ---------------------------------------------------------------
resource "azurerm_container_app_environment" "env" {
  name                       = "cae-flashsale"
  location                   = azurerm_resource_group.rg.location
  resource_group_name        = azurerm_resource_group.rg.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.law.id
}

locals {
  pg_connection = "Host=${azurerm_postgresql_flexible_server.db.fqdn};Port=5432;Database=FlashSaleDb;Username=psqladmin;Password=${coalesce(var.postgres_admin_password, random_password.pg.result)};Maximum Pool Size=80"
  image_base    = "${azurerm_container_registry.acr.login_server}/order-api"
}

resource "azurerm_container_app" "api" {
  name                         = "order-api"
  resource_group_name          = azurerm_resource_group.rg.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"

  template {
    min_replicas = 1
    max_replicas = 10

    container {
      name   = "order-api"
      image  = "${local.image_base}:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name        = "ConnectionStrings__DefaultConnection"
        secret_name = "pg-connection"
      }
      env {
        name  = "ConnectionStrings__Redis"
        value = "${azurerm_redis_cache.reservation.hostname}:6380,password=${azurerm_redis_cache.reservation.primary_access_key},ssl=true"
      }
      env {
        name        = "ConnectionStrings__ServiceBus"
        secret_name = "sb-connection"
      }

      liveness_probe {
        path             = "/healthz"
        port             = 8080
        transport        = "HTTP"
        initial_delay    = 5
        interval_seconds = 10
      }
    }
  }

  secret {
    name  = "pg-connection"
    value = local.pg_connection
  }
  secret {
    name  = "sb-connection"
    value = azurerm_servicebus_namespace.orders.default_primary_connection_string
  }
  secret {
    name  = "acr-password"
    value = azurerm_container_registry.acr.admin_password
  }

  registry {
    server               = azurerm_container_registry.acr.login_server
    username             = azurerm_container_registry.acr.admin_username
    password_secret_name = "acr-password"
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

# Worker shares the Service Bus contract; scale-to-zero friendly.
# In AKS (Project 03) KEDA drives this replica count from queue depth.
resource "azurerm_container_app" "worker" {
  name                         = "order-worker"
  resource_group_name          = azurerm_resource_group.rg.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"

  template {
    min_replicas = 0
    max_replicas = 5

    container {
      name   = "order-worker"
      image  = "${azurerm_container_registry.acr.login_server}/order-worker:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name        = "ConnectionStrings__DefaultConnection"
        secret_name = "pg-connection"
      }
      env {
        name        = "ConnectionStrings__ServiceBus"
        secret_name = "sb-connection"
      }
    }
  }

  secret {
    name  = "pg-connection"
    value = local.pg_connection
  }
  secret {
    name  = "sb-connection"
    value = azurerm_servicebus_namespace.orders.default_primary_connection_string
  }
  secret {
    name  = "acr-password"
    value = azurerm_container_registry.acr.admin_password
  }

  registry {
    server               = azurerm_container_registry.acr.login_server
    username             = azurerm_container_registry.acr.admin_username
    password_secret_name = "acr-password"
  }
}


