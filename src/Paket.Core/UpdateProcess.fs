﻿/// Contains methods for the update process.
module Paket.UpdateProcess

open Paket
open System.IO
open Paket.Domain
open Paket.PackageResolver
open System.Collections.Generic
open Chessie.ErrorHandling
open Paket.Logging

let addPackagesFromReferenceFiles projects (dependenciesFile : DependenciesFile) =
    let lockFileName = DependenciesFile.FindLockfile dependenciesFile.FileName
    let oldLockFile =
        if lockFileName.Exists then
            LockFile.LoadFrom(lockFileName.FullName)
        else
            LockFile.Create(lockFileName.FullName, dependenciesFile.Options, Resolution.Ok(Map.empty), [])

    let allExistingPackages =
        oldLockFile.ResolvedPackages
        |> Seq.map (fun d -> NormalizedPackageName d.Value.Name)
        |> Set.ofSeq

    let allReferencedPackages =
        projects
        |> Seq.collect (fun (_,referencesFile) -> referencesFile.NugetPackages)

    let diff =
        allReferencedPackages
        |> Seq.filter (fun p ->
            NormalizedPackageName p.Name
            |> allExistingPackages.Contains
            |> not)

    if Seq.isEmpty diff then
        dependenciesFile
    else
        let newDependenciesFile =
            diff
            |> Seq.fold (fun (dependenciesFile:DependenciesFile) dep ->
                if dependenciesFile.HasPackage dep.Name then
                    dependenciesFile
                else
                    dependenciesFile.AddAdditionalPackage(dep.Name,"",dep.Settings)) dependenciesFile
        newDependenciesFile.Save()
        newDependenciesFile

let selectiveUpdate resolve lockFile dependenciesFile updateAll package =
    let resolution =
        if updateAll then
            resolve dependenciesFile
        else
            let changedDependencies = DependencyChangeDetection.findChangesInDependenciesFile(dependenciesFile,lockFile)

            let changed =
                match package with
                | None -> changedDependencies
                | Some package -> Set.add package changedDependencies

            let dependenciesFile = DependencyChangeDetection.PinUnchangedDependencies dependenciesFile lockFile changed

            resolve dependenciesFile

    LockFile(lockFile.FileName, dependenciesFile.Options, resolution.ResolvedPackages.GetModelOrFail(), resolution.ResolvedSourceFiles)

let SelectiveUpdate(dependenciesFile : DependenciesFile, updateAll, exclude, force) =
    let oldLockFile = LockFile.LoadFrom <| dependenciesFile.FindLockfile().FullName 
    let lockFile = selectiveUpdate (fun d -> d.Resolve(force)) oldLockFile dependenciesFile updateAll exclude
    lockFile.Save()
    lockFile

/// Smart install command
let SmartInstall(dependenciesFileName, updateAll, exclude, options : UpdaterOptions) =
    let root = Path.GetDirectoryName dependenciesFileName
    let projects = InstallProcess.findAllReferencesFiles root |> returnOrFail
    let dependenciesFile = DependenciesFile.ReadFromFile(dependenciesFileName)

    let lockFile = SelectiveUpdate(dependenciesFile, updateAll, exclude, options.Common.Force)

    if not options.NoInstall then
        InstallProcess.InstallIntoProjects(
            dependenciesFile.GetAllPackageSources(),
            options.Common, lockFile, projects)

/// Update a single package command
let UpdatePackage(dependenciesFileName, packageName : PackageName, newVersion, options : UpdaterOptions) =
    match newVersion with
    | Some v ->
        DependenciesFile.ReadFromFile(dependenciesFileName)
            .UpdatePackageVersion(packageName, v)
            .Save()
    | None -> tracefn "Updating %s in %s" (packageName.ToString()) dependenciesFileName

    SmartInstall(dependenciesFileName, false, Some(NormalizedPackageName packageName), options)

/// Update command
let Update(dependenciesFileName, options : UpdaterOptions) =
    let lockFileName = DependenciesFile.FindLockfile dependenciesFileName
    
    SmartInstall(dependenciesFileName, true, None, options)
