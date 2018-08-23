﻿using System;
using System.Collections.Generic;
using System.ServiceProcess;
using SEBWindowsServiceContracts;
using SebWindowsServiceWCF.RegistryHandler;
using WUApiLib;

namespace SebWindowsServiceWCF.ServiceImplementations
{
	public class RegistryService : IRegistryServiceContract, IDisposable
	{
		//Simply returns true
		public bool TestServiceConnetcion()
		{
			return true;
		}

		/// <summary>
		/// Sets the registry values
		/// </summary>
		/// <param name="registryValues">The registry values to set</param>
		/// <param name="sid">The sid of the currently logged in user - needed to identify the correct registry key path</param>
		/// <param name="username">The username of the currently logged in user - needed to identify the correct registry key path</param>
		/// <returns>true if all operations succeeded, false if something went wrong. See the logfile for details then.</returns>
		public bool SetRegistryEntries(Dictionary<RegistryIdentifiers, object> registryValues, string sid, string username)
		{
			bool res = true;

			try
			{
				Logger.Log("SID: " + (sid ?? "<NULL>"));
				Logger.Log("Username: " + (username ?? "<NULL>"));

				if (String.IsNullOrEmpty(sid) && String.IsNullOrEmpty(username))
				{
					Logger.Log("Cannot set registry entries without SID or username information!");

					return false;
				}

				if (string.IsNullOrEmpty(sid))
				{
					sid = SIDHandler.GetSIDFromUsername(username);

					if (String.IsNullOrWhiteSpace(sid))
					{
						Logger.Log($"Failed to set registry entries because SID could not be determined for user '{username}''!");

						return false;
					}
					else
					{
						Logger.Log("SID from username: " + (sid ?? "<NULL>"));
					}
				}

				using (var persistentRegistryFile = new PersistentRegistryFile(username, sid))
				{
					Logger.Log("Attempting to set new registry values...");

					foreach (var registryValue in registryValues)
					{
						RegistryEntry regEntry;
						
						//If the class could not be instantiated it means either reflection did not work properly or the registry-class does not exists
						//don't interrupt the whole process but set the return value to false to indicate a possible error
						try
						{
							//Use Reflection
							var type = Type.GetType(String.Format("SebWindowsServiceWCF.RegistryHandler.Reg{0}", registryValue.Key));
							if (type == null) continue;
							regEntry = (RegistryEntry)Activator.CreateInstance(type, sid);
						}
						catch (Exception ex)
						{
							Logger.Log(ex, String.Format("Unable to instantiate registryclass: {0}", registryValue.Key));
							res = false;
							continue;
						}

						//If the registry value could not have been set correctly or something went wrong with the persistent registry file
						//don't interrupt the whole process but set the return value to false to indicate a possible error
						//but never change a registry key without successfully write the persistent registry file
						try
						{
							//If there is nothing to change, then do not change anything
							if (object.Equals(registryValue.Value, regEntry.GetValue()))
							{
								Logger.Log(String.Format("Registry key '{0}\\{1}' already has value '{2}', skipping it.", regEntry.RegistryPath, regEntry.DataItemName, registryValue.Value ?? "<NULL>"));

								continue;
							}

							//Only store the entry in the persistent file if not already existing
							if (!persistentRegistryFile.FileContent.RegistryValues.ContainsKey(registryValue.Key))
							{
								persistentRegistryFile.FileContent.RegistryValues.Add(registryValue.Key, regEntry.GetValue());
								//Save after every change
								persistentRegistryFile.Save();
							}

							//Change the registry value if all operations succeeded until here
							if (registryValue.Value is null)
							{
								regEntry.Delete();
								Logger.Log($"Deleted registry key '{regEntry.RegistryPath}\\{regEntry.DataItemName}'.");
							}
							else
							{
								regEntry.SetValue(registryValue.Value);
								Logger.Log($"Set registry key '{regEntry.RegistryPath}\\{regEntry.DataItemName}' to '{registryValue.Value}'.");
							}
						}
						catch (Exception ex)
						{
							Logger.Log(ex, String.Format("Unable to set the registry value for '{0}\\{1}': {2}: {3}", regEntry.RegistryPath, regEntry.DataItemName, ex.Message, ex.StackTrace));
							res = false;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Log(ex, string.Format("Unable to set Registry value: {0}: {1}", ex.Message, ex.StackTrace));
				res = false;
			}
			
			return res;
		}

		/// <summary>
		/// Resets the registry values if a PersistentRegistryFile is existing
		/// </summary>
		/// <returns></returns>
		public bool Reset()
		{
			var success = true;

			try
			{
				Logger.Log("Attempting to reset registry values...");

				using (var persistentRegistryFile = new PersistentRegistryFile())
				{
					if (persistentRegistryFile.FileContent.Username != null)
					{
						success = ResetRegistryEntries(persistentRegistryFile);

						if (persistentRegistryFile.FileContent.EnableWindowsUpdate)
						{
							SetWindowsUpdate(true);
						}

						if (success)
						{
							persistentRegistryFile.Delete();
						}
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Log(ex, string.Format("Unable to reset registry values: {0} : {1}", ex.Message, ex.StackTrace));
				success = false;
			}

			return success;
		}

		/// <summary>
		/// Disables the automatic windows update service
		/// </summary>
		public bool DisableWindowsUpdate()
		{
			if (SetWindowsUpdate(false))
			{
				using (var persistentRegistryFile = new PersistentRegistryFile())
				{
					persistentRegistryFile.FileContent.EnableWindowsUpdate = true;
					persistentRegistryFile.Save();
				}
				return true;
			}
			return false;
		}

		private bool ResetRegistryEntries(PersistentRegistryFile persistentRegistryFile)
		{
			var success = true;
			var sid = persistentRegistryFile.FileContent.SID;
			var username = persistentRegistryFile.FileContent.Username;
			var originalValues = new Dictionary<RegistryIdentifiers, object>(persistentRegistryFile.FileContent.RegistryValues);

			try
			{
				Logger.Log("SID: " + (sid ?? "<NULL>"));
				Logger.Log("Username: " + (username ?? "<NULL>"));

				if (String.IsNullOrEmpty(sid) && String.IsNullOrEmpty(username))
				{
					Logger.Log("Cannot reset registry entries without SID or username information!");

					return false;
				}

				if (String.IsNullOrEmpty(sid))
				{
					sid = SIDHandler.GetSIDFromUsername(username);

					if (String.IsNullOrWhiteSpace(sid))
					{
						Logger.Log($"Failed to reset registry entries because SID could not be determined for user '{username}''!");

						return false;
					}
					else
					{
						Logger.Log("SID from username: " + (sid ?? "<NULL>"));
					}
				}

				foreach (var originalValue in originalValues)
				{
					RegistryEntry entry;

					try
					{
						var type = Type.GetType(String.Format("SebWindowsServiceWCF.RegistryHandler.Reg{0}", originalValue.Key));

						entry = (RegistryEntry) Activator.CreateInstance(type, sid);
					}
					catch (Exception ex)
					{
						Logger.Log(ex, String.Format("Unable to instantiate registryclass: {0}", originalValue.Key));
						success = false;

						continue;
					}

					try
					{
						if (object.Equals(originalValue.Value, entry.GetValue()))
						{
							Logger.Log(String.Format("Registry key '{0}\\{1}' already has original value '{2}', skipping it.", entry.RegistryPath, entry.DataItemName, originalValue.Value ?? "<NULL>"));
						}
						else if (originalValue.Value is null)
						{
							entry.Delete();
							Logger.Log($"Deleted registry key '{entry.RegistryPath}\\{entry.DataItemName}'.");
						}
						else
						{
							entry.SetValue(originalValue.Value);
							Logger.Log($"Set registry key '{entry.RegistryPath}\\{entry.DataItemName}' to '{originalValue.Value}'.");
						}

						persistentRegistryFile.FileContent.RegistryValues.Remove(originalValue.Key);
						persistentRegistryFile.Save();
					}
					catch (Exception ex)
					{
						Logger.Log(ex, String.Format("Unable to reset the registry value for '{0}\\{1}': {2}: {3}", entry.RegistryPath, entry.DataItemName, ex.Message, ex.StackTrace));
						success = false;
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Log(ex, string.Format("Unable to reset registry value: {0}: {1}", ex.Message, ex.StackTrace));
				success = false;
			}

			Logger.Log("Initiating group policy update...");
			new CommandExecutor.CommandExecutor().ExecuteCommandAsync("gpupdate /force");

			return success;
		}

		private bool SetWindowsUpdate(bool enable)
		{
			if (OSVersion.FriendlyName().Contains("10"))
			{
				//Stop per Windows Service on windows 10
				ServiceController service = new ServiceController("wuauserv");
				try
				{
					TimeSpan timeout = TimeSpan.FromMilliseconds(500);
					if (enable)
					{
						if (service.Status != ServiceControllerStatus.StartPending && service.Status != ServiceControllerStatus.Running)
						{
							service.Start();
							Logger.Log($"Started service '{service.DisplayName}'.");
							//service.WaitForStatus(ServiceControllerStatus.Running, timeout);
						}
						else
						{
							Logger.Log($"Service '{service.DisplayName}' is already starting / running...");
						}
					}
					else
					{
						if (service.Status != ServiceControllerStatus.StopPending && service.Status != ServiceControllerStatus.Stopped)
						{
							service.Stop();
							Logger.Log($"Stopped service '{service.DisplayName}'.");
							//service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
						}
						else
						{
							Logger.Log($"Service '{service.DisplayName}' is already stopping / stopped...");
						}
					}

					Logger.Log(String.Format("Set windows update to {0}", enable));

					return true;
				}
				catch (Exception ex)
				{
                    Logger.Log(string.Format("Unable to disable Windows Update: {0} : ", ex.Message, ex.StackTrace));
					return false;
				}
			}

			//Use Automatic Update Class
			try
			{
				var auc = new AutomaticUpdates();

				if (enable)
					auc.Resume();
				else
					auc.Pause();

				Logger.Log(String.Format("Set windows update to {0}", enable));

				return true;
			}
			catch (Exception ex)
			{
                Logger.Log(string.Format("Unable to disable Windows Update: {0} : ", ex.Message, ex.StackTrace));
                return false;
			}            
		}


		public void Dispose()
		{
		}
	}
}
