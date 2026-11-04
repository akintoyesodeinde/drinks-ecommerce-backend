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
    [string]$ExpectedAccountId = "402535053369",
    [string]$Region = "us-east-1",
    [string]$InstanceType = "t3.medium",
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
$existingProviders = aws iam list-open-id-connect-providers --output json | ConvertFrom-Json
$hasGithubOidc = $existingProviders.OpenIDConnectProviderList | Where-Object { $_.Arn -eq $oidcArn }

if (-not $hasGithubOidc) {
    aws iam create-open-id-connect-provider `
        --url "https://token.actions.githubusercontent.com" `
        --client-id-list "sts.amazonaws.com" `
        --thumbprint-list "6938fd4d98bab03faadb97b34396831e3780aea1" `
        --tags Key=Project,Value=DrinksEcommerce | Out-Null
    Write-Host "Created GitHub OIDC Provider: $oidcArn" -ForegroundColor Green
} else {
    Write-Host "GitHub OIDC Provider already exists: $oidcArn" -ForegroundColor Green
}

Write-Host "`n=== 3. Configuring Least-Privilege GitHub Actions Deploy Role ===" -ForegroundColor Cyan
$ghRoleName = "DrinksEcommerce-GitHubActions-Deploy"
$ghTrustFile = Join-Path $ScriptDir "iam-github-oidc-trust-policy.json"
$ghPermFile  = Join-Path $ScriptDir "iam-github-deploy-permissions.json"

aws iam get-role --role-name $ghRoleName 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    aws iam create-role `
        --role-name $ghRoleName `
        --assume-role-policy-document "file://$ghTrustFile" `
        --description "Least-privilege OIDC role for DrinksEcommerce GitHub Actions SSM deployment" `
        --tags Key=Project,Value=DrinksEcommerce | Out-Null
} else {
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

aws iam get-role --role-name $ec2RoleName 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    aws iam create-role `
        --role-name $ec2RoleName `
        --assume-role-policy-document "file://$tempEc2Trust" `
        --description "Least-privilege runtime role for DrinksEcommerce EC2 (SSM, CloudWatch, Prod Secrets)" `
        --tags Key=Project,Value=DrinksEcommerce | Out-Null
}

aws iam attach-role-policy --role-name $ec2RoleName --policy-arn "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore"
aws iam attach-role-policy --role-name $ec2RoleName --policy-arn "arn:aws:iam::aws:policy/CloudWatchAgentServerPolicy"
aws iam put-role-policy `
    --role-name $ec2RoleName `
    --policy-name "DrinksEcommerceSecretsReadPolicy" `
    --policy-document "file://$ec2SecretsPolicyFile" | Out-Null

aws iam get-instance-profile --instance-profile-name $ec2ProfileName 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    aws iam create-instance-profile --instance-profile-name $ec2ProfileName | Out-Null
    aws iam add-role-to-instance-profile --instance-profile-name $ec2ProfileName --role-name $ec2RoleName | Out-Null
    Start-Sleep -Seconds 10
}
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
    Write-Host "`n=== 7. Launching EC2 Instance from Launch Template $ltName ===" -ForegroundColor Cyan
    $runResult = aws ec2 run-instances `
        --region $Region `
        --launch-template "LaunchTemplateName=$ltName,Version=`$Default" `
        --min-count 1 --max-count 1 `
        --output json | ConvertFrom-Json
    $instanceId = $runResult.Instances[0].InstanceId
    Write-Host "Launched EC2 Instance: $instanceId. Waiting for running state..." -ForegroundColor Yellow
    aws ec2 wait instance-running --region $Region --instance-ids $instanceId
    $publicIp = (aws ec2 describe-instances --region $Region --instance-ids $instanceId --query "Reservations[0].Instances[0].PublicIpAddress" --output text).Trim()
    Write-Host "EC2 Instance Running! InstanceId=$instanceId | PublicIP=$publicIp" -ForegroundColor Green
}
