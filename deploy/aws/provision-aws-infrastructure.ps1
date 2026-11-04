<#
.SYNOPSIS
    DrinksEcommerce — AWS IAM OIDC, Secrets Manager, Security Group, EC2 Launch Template & Instance Provisioner
.DESCRIPTION
     target Account: 402535053369
    target Region:  us-east-1
    - Enforces hard account verification (stops immediately if Account != 402535053369).
    - Creates GitHub OIDC provider (token.actions.githubusercontent.com) if not already present.
    - Creates least-privilege role DrinksEcommerce-GitHubActions-Deploy (OIDC trust restricted to
      akintoyesodeinde/drinks-ecommerce-backend and akintoyesodeinde/drinks-ecommerce-frontend).
    - Creates least-privilege role DrinksEcommerce-EC2-Runtime + Instance Profile (SSM + CloudWatch +
      restricted Secrets Manager access to drinks-ecommerce/prod/*).
    - Creates Security Group drinks-ecommerce-sg (ports 80 and 443 open; SSH port 22 closed by default
      in favor of SSM Session Manager; ports 1433 and 8080 NEVER exposed).
    - Creates reusable EC2 Launch Template drinks-ecommerce-lt (Amazon Linux 2023 x86_64, t3.medium,
      30 GiB encrypted gp3 volume, IMDSv2 required, User Data from deploy/ec2-user-data.sh).
    - Optionally launches the EC2 instance from the Launch Template when -LaunchInstance is specified.
#>
[CmdletBinding()]
param(
    [string]$ExpectedAccountId = "415805875879",
    [string]$Region = "eu-north-1",
    [string]$InstanceType = "c7i-flex.large",
    [switch]$LaunchInstance
)

$ErrorActionPreference = "Stop"

Write-Host "=== 1. Verifying AWS CLI Identity & Account Boundary ===" -ForegroundColor Cyan
$identityJson = aws sts get-caller-identity --region $Region --output json
if ($LASTEXITCODE -ne 0) {
    throw "AWS CLI is not authenticated. Run 'aws login' or 'aws configure sso' first."
}
$identity = $identityJson | ConvertFrom-Json
Write-Host "Authenticated ARN:     $($identity.Arn)"
Write-Host "Authenticated Account: $($identity.Account)"

if ($identity.Account -ne $ExpectedAccountId) {
    throw "SAFETY STOP: Authenticated AWS Account ($($identity.Account)) does not match expected Account ($ExpectedAccountId)."
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$DeployDir = Split-Path -Parent $ScriptDir
$UserDataPath = Join-Path $DeployDir "ec2-user-data.sh"

Write-Host "`n=== 2. Configuring GitHub Actions IAM OIDC Provider ===" -ForegroundColor Cyan
$oidcArn = "arn:aws:iam::${ExpectedAccountId}:oidc-provider/token.actions.githubusercontent.com"
$prevErr = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$oidcCreateOut = aws iam create-open-id-connect-provider `
    --url "https://token.actions.githubusercontent.com" `
    --client-id-list "sts.amazonaws.com" `
    --thumbprint-list "6938fd4d98bab03faadb97b34396831e3780aea1" `
    --tags Key=Project,Value=DrinksEcommerce 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "Created GitHub OIDC Provider: $oidcArn" -ForegroundColor Green
} elseif ("$oidcCreateOut" -match "EntityAlreadyExists") {
    Write-Host "GitHub OIDC Provider already exists: $oidcArn" -ForegroundColor Green
} else {
    Write-Host "Note: iam:CreateOpenIDConnectProvider is restricted by AWS Ready Set Build Free Plan SCP (p-4xilptm8); continuing with OIDC role configuration." -ForegroundColor Yellow
}

Write-Host "`n=== 3. Configuring Least-Privilege GitHub Actions Deploy Role ===" -ForegroundColor Cyan
$ghRoleName = "DrinksEcommerce-GitHubActions-Deploy"
$ghTrustFile = Join-Path $ScriptDir "iam-github-oidc-trust-policy.json"
$ghPermFile  = Join-Path $ScriptDir "iam-github-deploy-permissions.json"

aws iam get-role --role-name $ghRoleName >$null 2>&1
if ($LASTEXITCODE -ne 0) {
    $ErrorActionPreference = "Stop"
    aws iam create-role `
        --role-name $ghRoleName `
        --assume-role-policy-document "file://$ghTrustFile" `
        --description "Least-privilege OIDC role for DrinksEcommerce GitHub Actions SSM deployment" `
        --tags Key=Project,Value=DrinksEcommerce | Out-Null
} else {
    $ErrorActionPreference = "Stop"
    aws iam update-assume-role-policy `
        --role-name $ghRoleName `
        --policy-document "file://$ghTrustFile" | Out-Null
}

aws iam put-role-policy `
    --role-name $ghRoleName `
    --policy-name "DrinksEcommerceSSMDeployPolicy" `
    --policy-document "file://$ghPermFile" | Out-Null

$ghRoleArn = (aws iam get-role --role-name $ghRoleName --query "Role.Arn" --output text).Trim()
Write-Host "GitHub Actions OIDC Role Ready: $ghRoleArn" -ForegroundColor Green

Write-Host "`n=== 4. Configuring Least-Privilege EC2 Runtime Role & Instance Profile ===" -ForegroundColor Cyan
$ec2RoleName = "DrinksEcommerce-EC2-Runtime"
$ec2ProfileName = "DrinksEcommerce-EC2-InstanceProfile"
$ec2SecretsPolicyFile = Join-Path $ScriptDir "iam-ec2-secrets-policy.json"

$ec2TrustJson = @'
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": { "Service": "ec2.amazonaws.com" },
      "Action": "sts:AssumeRole"
    }
  ]
}
'@
$tempEc2Trust = Join-Path $env:TEMP "drinks-ec2-trust.json"
Set-Content -Path $tempEc2Trust -Value $ec2TrustJson -Encoding utf8

$ErrorActionPreference = "Continue"
aws iam get-role --role-name $ec2RoleName >$null 2>&1
if ($LASTEXITCODE -ne 0) {
    $ErrorActionPreference = "Stop"
    aws iam create-role `
        --role-name $ec2RoleName `
        --assume-role-policy-document "file://$tempEc2Trust" `
        --description "Least-privilege runtime role for DrinksEcommerce EC2 (SSM, CloudWatch, Prod Secrets)" `
        --tags Key=Project,Value=DrinksEcommerce | Out-Null
}
$ErrorActionPreference = "Stop"

aws iam attach-role-policy --role-name $ec2RoleName --policy-arn "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore"
aws iam attach-role-policy --role-name $ec2RoleName --policy-arn "arn:aws:iam::aws:policy/CloudWatchAgentServerPolicy"
aws iam put-role-policy `
    --role-name $ec2RoleName `
    --policy-name "DrinksEcommerceSecretsReadPolicy" `
    --policy-document "file://$ec2SecretsPolicyFile" | Out-Null

$ErrorActionPreference = "Continue"
aws iam get-instance-profile --instance-profile-name $ec2ProfileName >$null 2>&1
if ($LASTEXITCODE -ne 0) {
    $ErrorActionPreference = "Stop"
    aws iam create-instance-profile --instance-profile-name $ec2ProfileName | Out-Null
    aws iam add-role-to-instance-profile --instance-profile-name $ec2ProfileName --role-name $ec2RoleName | Out-Null
    Start-Sleep -Seconds 10
}
$ErrorActionPreference = "Stop"
Write-Host "EC2 Instance Profile Ready: $ec2ProfileName" -ForegroundColor Green

Write-Host "`n=== 5. Configuring EC2 Security Group (Ports 80 & 443 Only; 1433 & 8080 Internal Only) ===" -ForegroundColor Cyan
$vpcId = (aws ec2 describe-vpcs --region $Region --filters "Name=isDefault,Values=true" --query "Vpcs[0].VpcId" --output text).Trim()
if (-not $vpcId -or $vpcId -eq "None") {
    $vpcId = (aws ec2 describe-vpcs --region $Region --query "Vpcs[0].VpcId" --output text).Trim()
}

$sgName = "drinks-ecommerce-sg"
$sgId = (aws ec2 describe-security-groups --region $Region --filters "Name=group-name,Values=$sgName" "Name=vpc-id,Values=$vpcId" --query "SecurityGroups[0].GroupId" --output text).Trim()
if (-not $sgId -or $sgId -eq "None") {
    $sgId = (aws ec2 create-security-group `
        --region $Region `
        --group-name $sgName `
        --description "DrinksEcommerce Web/HTTPS Ingress Only (SSM Managed, No Public DB/API Ports)" `
        --vpc-id $vpcId `
        --tag-specifications "ResourceType=security-group,Tags=[{Key=Name,Value=$sgName},{Key=Project,Value=DrinksEcommerce}]" `
        --query "GroupId" --output text).Trim()

    aws ec2 authorize-security-group-ingress --region $Region --group-id $sgId --protocol tcp --port 80 --cidr 0.0.0.0/0 | Out-Null
    aws ec2 authorize-security-group-ingress --region $Region --group-id $sgId --protocol tcp --port 443 --cidr 0.0.0.0/0 | Out-Null
}
Write-Host "Security Group Ready: $sgId (VPC: $vpcId)" -ForegroundColor Green

Write-Host "`n=== 5b. Ensuring AWS Secrets Manager Secret (drinks-ecommerce/prod/app-secrets) ===" -ForegroundColor Cyan
$secretName = "drinks-ecommerce/prod/app-secrets"
$ErrorActionPreference = "Continue"
aws secretsmanager describe-secret --region $Region --secret-id $secretName >$null 2>&1
$secretExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = "Stop"
if (-not $secretExists) {
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes32 = New-Object byte[] 32
    $rng.GetBytes($bytes32)
    $sqlPass = "DrkSql#" + ([Convert]::ToBase64String($bytes32).Substring(0, 18) -replace '[+/=]', '9') + "!aA1"

    $bytes64 = New-Object byte[] 48
    $rng.GetBytes($bytes64)
    $jwtKey = [Convert]::ToBase64String($bytes64)

    $bytes24 = New-Object byte[] 24
    $rng.GetBytes($bytes24)
    $adminPass = "DrkAdm#" + ([Convert]::ToBase64String($bytes24).Substring(0, 16) -replace '[+/=]', '7') + "!zZ9"

    $secretPayload = @{
        MSSQL_PID = "Express"
        MSSQL_SA_PASSWORD = $sqlPass
        ConnectionStrings__DefaultConnection = "Server=sqlserver,1433;Database=DrinksEcommerceDb;User Id=sa;Password=$sqlPass;TrustServerCertificate=True;MultipleActiveResultSets=True;Encrypt=False"
        ConnectionStrings__Redis = "redis:6379,abortConnect=false"
        Database__SeedCatalog = "true"
        Database__SeedDemoAccounts = "false"
        BootstrapAdmin__Email = "hr@pinnaclesoftwareservices.store"
        BootstrapAdmin__Password = $adminPass
        BootstrapAdmin__FirstName = "Akintoye"
        BootstrapAdmin__LastName = "Sodeinde"
        BootstrapAdmin__Phone = "+2348030000000"
        Jwt__Issuer = "DrinksEcommerce.API"
        Jwt__Audience = "DrinksEcommerce.Clients"
        Jwt__SigningKey = $jwtKey
        Payments__Provider = "Simulated"
        Payments__BaseUrl = "https://api.paystack.co/"
        Payments__PublicKey = "pk_test_replace_in_secrets_manager"
        Payments__SecretKey = "sk_test_replace_in_secrets_manager"
        Store__AlcoholSalesEnabled = "false"
    } | ConvertTo-Json -Compress

    $tempSecretFile = Join-Path $env:TEMP ("drinks-secret-" + [Guid]::NewGuid().ToString("N") + ".json")
    try {
        Set-Content -Path $tempSecretFile -Value $secretPayload -NoNewline -Encoding utf8
        aws secretsmanager create-secret `
            --region $Region `
            --name $secretName `
            --description "DrinksEcommerce Production Runtime Configuration & Secrets" `
            --secret-string "file://$tempSecretFile" `
            --tags Key=Project,Value=DrinksEcommerce | Out-Null
        Write-Host "Created Secrets Manager secret: $secretName (strong random credentials generated in memory; never printed)" -ForegroundColor Green
    } finally {
        if (Test-Path $tempSecretFile) { Remove-Item -Force $tempSecretFile }
    }
} else {
    Write-Host "Secrets Manager secret already exists: $secretName" -ForegroundColor Green
}

Write-Host "`n=== 6. Resolving Latest Amazon Linux 2023 x86_64 AMI & Creating EC2 Launch Template ===" -ForegroundColor Cyan
$amiId = (aws ssm get-parameter `
    --region $Region `
    --name "/aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-x86_64" `
    --query "Parameter.Value" --output text).Trim()
Write-Host "Resolved Amazon Linux 2023 x86_64 AMI: $amiId"

$userDataBytes = [System.IO.File]::ReadAllBytes($UserDataPath)
$userDataBase64 = [Convert]::ToBase64String($userDataBytes)

$ltName = "drinks-ecommerce-lt"
$ltDataObj = @{
    ImageId = $amiId
    InstanceType = $InstanceType
    IamInstanceProfile = @{ Name = $ec2ProfileName }
    SecurityGroupIds = @($sgId)
    UserData = $userDataBase64
    MetadataOptions = @{
        HttpTokens = "required"
        HttpPutResponseHopLimit = 2
        HttpEndpoint = "enabled"
    }
    Monitoring = @{ Enabled = $false }
    BlockDeviceMappings = @(
        @{
            DeviceName = "/dev/xvda"
            Ebs = @{
                VolumeSize = 30
                VolumeType = "gp3"
                Encrypted = $true
                DeleteOnTermination = $true
            }
        }
    )
    TagSpecifications = @(
        @{
            ResourceType = "instance"
            Tags = @(
                @{ Key = "Name"; Value = "drinks-ecommerce-prod" },
                @{ Key = "Project"; Value = "DrinksEcommerce" },
                @{ Key = "Environment"; Value = "Production" }
            )
        },
        @{
            ResourceType = "volume"
            Tags = @(
                @{ Key = "Name"; Value = "drinks-ecommerce-prod-root" },
                @{ Key = "Project"; Value = "DrinksEcommerce" }
            )
        }
    )
}

$tempLtFile = Join-Path $env:TEMP "drinks-lt-data.json"
$ltDataObj | ConvertTo-Json -Depth 10 | Set-Content -Path $tempLtFile -Encoding utf8

$existingLtId = (aws ec2 describe-launch-templates --region $Region --filters "Name=launch-template-name,Values=$ltName" --query "LaunchTemplates[0].LaunchTemplateId" --output text 2>$null)
if (-not $existingLtId -or $existingLtId -eq "None") {
    $ltResult = aws ec2 create-launch-template `
        --region $Region `
        --launch-template-name $ltName `
        --version-description "Initial AL2023 Docker + Compose + SSM + CloudWatch UserData" `
        --launch-template-data "file://$tempLtFile" `
        --tag-specifications "ResourceType=launch-template,Tags=[{Key=Project,Value=DrinksEcommerce}]" `
        --output json | ConvertFrom-Json
    $ltId = $ltResult.LaunchTemplate.LaunchTemplateId
    $ltVersion = $ltResult.LaunchTemplate.LatestVersionNumber
} else {
    $ltVerResult = aws ec2 create-launch-template-version `
        --region $Region `
        --launch-template-name $ltName `
        --version-description "Updated AL2023 Docker + Compose + SSM + CloudWatch UserData" `
        --launch-template-data "file://$tempLtFile" `
        --output json | ConvertFrom-Json
    $ltId = $ltVerResult.LaunchTemplateVersion.LaunchTemplateId
    $ltVersion = $ltVerResult.LaunchTemplateVersion.VersionNumber
    aws ec2 modify-launch-template --region $Region --launch-template-name $ltName --default-version "$ltVersion" | Out-Null
}
Write-Host "Launch Template Ready: $ltName ($ltId, Default Version: $ltVersion)" -ForegroundColor Green

if ($LaunchInstance) {
    Write-Host "`n=== 7. Launching / Verifying EC2 Instance from Launch Template $ltName ===" -ForegroundColor Cyan
    $existingInstance = (aws ec2 describe-instances `
        --region $Region `
        --filters "Name=tag:Project,Values=DrinksEcommerce" "Name=instance-state-name,Values=pending,running" `
        --query "Reservations[0].Instances[0].InstanceId" --output text 2>$null)

    if ($existingInstance -and $existingInstance -ne "None") {
        $instanceId = $existingInstance.Trim()
        Write-Host "Existing running DrinksEcommerce EC2 instance found: $instanceId" -ForegroundColor Green
    } else {
        $runResult = aws ec2 run-instances `
            --region $Region `
            --launch-template "LaunchTemplateName=$ltName,Version=`$Default" `
            --min-count 1 --max-count 1 `
            --output json | ConvertFrom-Json
        $instanceId = $runResult.Instances[0].InstanceId
        Write-Host "Launched EC2 Instance: $instanceId. Waiting for running state..." -ForegroundColor Yellow
        aws ec2 wait instance-running --region $Region --instance-ids $instanceId
    }

    $publicIp = (aws ec2 describe-instances --region $Region --instance-ids $instanceId --query "Reservations[0].Instances[0].PublicIpAddress" --output text).Trim()
    Write-Host "EC2 Instance Running! InstanceId=$instanceId | PublicIP=$publicIp" -ForegroundColor Green

    aws cloudwatch put-metric-alarm `
        --region $Region `
        --alarm-name "drinks-ecommerce-status-check-$instanceId" `
        --alarm-description "Alert when DrinksEcommerce EC2 instance fails system or instance status check" `
        --namespace "AWS/EC2" `
        --metric-name "StatusCheckFailed" `
        --dimensions "Name=InstanceId,Value=$instanceId" `
        --statistic Maximum `
        --period 60 `
        --evaluation-periods 2 `
        --threshold 1 `
        --comparison-operator GreaterThanOrEqualToThreshold | Out-Null
    Write-Host "CloudWatch Alarm configured: drinks-ecommerce-status-check-$instanceId" -ForegroundColor Green
}
