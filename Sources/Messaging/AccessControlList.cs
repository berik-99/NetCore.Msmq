using System;
using System.Security.Permissions;
using System.Security;
using System.Collections;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text;
using Msmq.NetCore.Messaging.Interop;

namespace Msmq.NetCore.Messaging;

public class AccessControlList : CollectionBase
{
	internal static readonly int UnknownEnvironment = 0;
	internal static readonly int W2kEnvironment = 1;
	internal static readonly int NtEnvironment = 2;
	internal static readonly int NonNtEnvironment = 3;

	// Double-checked locking pattern requires volatile for read/write synchronization
	private static volatile int environment = UnknownEnvironment;

	private static readonly object staticLock = new object();

	internal static int CurrentEnvironment
	{
		get
		{
			if (environment == UnknownEnvironment)
			{
				lock (staticLock)
				{
					if (environment == UnknownEnvironment)
					{
						EnvironmentPermission environmentPermission = new EnvironmentPermission(PermissionState.Unrestricted);
						environmentPermission.Assert();
						try
						{
							if (Environment.OSVersion.Platform == PlatformID.Win32NT)
							{
								if (Environment.OSVersion.Version.Major >= 5)
									environment = W2kEnvironment;
								else
									environment = NtEnvironment;
							}
							else
							{
								environment = NonNtEnvironment;
							}
						}
						finally
						{
							CodeAccessPermission.RevertAssert();
						}
					}
				}
			}

			return environment;
		}
	}

	public int Add(AccessControlEntry entry)
	{
		return List.Add(entry);
	}

	public void Insert(int index, AccessControlEntry entry)
	{
		List.Insert(index, entry);
	}

	public int IndexOf(AccessControlEntry entry)
	{
		return List.IndexOf(entry);
	}

	internal static void CheckEnvironment()
	{
		if (CurrentEnvironment == NonNtEnvironment)
			throw new PlatformNotSupportedException(Res.GetString(Res.WinNTRequired));
	}

	public bool Contains(AccessControlEntry entry)
	{
		return List.Contains(entry);
	}

	public void Remove(AccessControlEntry entry)
	{
		List.Remove(entry);
	}

	public void CopyTo(AccessControlEntry[] array, int index)
	{
		List.CopyTo(array, index);
	}

	internal IntPtr MakeAcl(IntPtr oldAcl)
	{
		CheckEnvironment();

		int ACECount = List.Count;
		IntPtr newAcl;

		NativeMethods.ExplicitAccess[] entries = new NativeMethods.ExplicitAccess[ACECount];

		GCHandle mem = GCHandle.Alloc(entries, GCHandleType.Pinned);
		try
		{
			for (int i = 0; i < ACECount; i++)
			{
				int sidSize = 0;
				int sidtype;
				int domainSize = 0;

				AccessControlEntry ace = (AccessControlEntry)List[i];

				if (ace.Trustee == null)
					throw new InvalidOperationException(Res.GetString(Res.InvalidTrustee));

				string name = ace.Trustee.Name;
				if (name == null)
					throw new InvalidOperationException(Res.GetString(Res.InvalidTrusteeName));

				if (ace.Trustee.TrusteeType == TrusteeType.Computer && !name.EndsWith("$"))
					name += "$";

				if (!UnsafeNativeMethods.LookupAccountName(ace.Trustee.SystemName, name, (IntPtr)0, ref sidSize, null, ref domainSize, out sidtype))
				{
					int errval = Marshal.GetLastWin32Error();
					if (errval != 122)
						throw new InvalidOperationException(Res.GetString(Res.CouldntResolve, ace.Trustee.Name, errval));
				}

				entries[i].data = Marshal.AllocHGlobal(sidSize);

				StringBuilder domainName = new StringBuilder(domainSize);
				if (!UnsafeNativeMethods.LookupAccountName(ace.Trustee.SystemName, name, entries[i].data, ref sidSize, domainName, ref domainSize, out sidtype))
					throw new InvalidOperationException(Res.GetString(Res.CouldntResolveName, ace.Trustee.Name));

				entries[i].grfAccessPermissions = ace.accessFlags;
				entries[i].grfAccessMode = (int)ace.EntryType;
				entries[i].grfInheritance = 0;
				entries[i].pMultipleTrustees = (IntPtr)0;
				entries[i].MultipleTrusteeOperation = NativeMethods.NO_MULTIPLE_TRUSTEE;
				entries[i].TrusteeForm = NativeMethods.TRUSTEE_IS_SID;
				entries[i].TrusteeType = (int)ace.Trustee.TrusteeType;
			}

			int err = SafeNativeMethods.SetEntriesInAclW(ACECount, mem.AddrOfPinnedObject(), oldAcl, out newAcl);

			if (err != NativeMethods.ERROR_SUCCESS)
				throw new Win32Exception(err);
		}
		finally
		{
			mem.Free();

			for (int i = 0; i < ACECount; i++)
			{
				if (entries[i].data != (IntPtr)0)
					Marshal.FreeHGlobal(entries[i].data);
			}
		}

		return newAcl;
	}

	internal static void FreeAcl(IntPtr acl)
	{
		SafeNativeMethods.LocalFree(acl);
	}
}
